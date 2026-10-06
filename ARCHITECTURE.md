# How DocWizz works

DocWizz is a code intelligence model that happens to generate documentation. Scanners turn source and project
files into one language-neutral graph of facts. Everything else reads that graph: documentation analysis,
architecture rules, change impact, and the Markdown generator. AI is an optional last step that turns facts into
prose. It is never a source of facts.

```text
.cs  .vue/.ts  .csproj/package.json  appsettings*.json/.env
  │      │              │                     │
CSharpScanner  scanner-vue (Node)  Projects  Configuration        ← facts only
  └──────┴───────┬──────┴─────────────┘
                 ▼
   link: HTTP calls → endpoints (Frontend.LinkHttp), external systems (Externals.Link),
         config-addressed HTTP clients (Configuration.Link), test code → `tests` edges
                 ▼
            CodeModel (nodes + edges)            → docwizz scan
      ┌──────────┼───────────────┐
      ▼          ▼               ▼
  Analyzer   Architecture       Diff             → analyze / check / architecture / diff
      └────┬─────┘
           ▼
       Generator  (+ AiProse drafts, cached)     → generate
           ▼
   docs/*.md, docs/.docwizz/{model,documentation}.json
```

`Program.cs` is the CLI and `BuildModel`, which runs the scanners and link steps in that order. [Setup.cs](src/DocWizz/Setup.cs) backs `docwizz setup`. It detects the stack from the model, narrows the default
`docwizz.yaml` to the layers and test globs that files match, and runs the stages in order, isolating failures.

## The code model

[CodeModel.cs](src/DocWizz/CodeModel.cs) has two records. A `Node` has an id, kind, name, source location and
optional facts: visibility, doc comment XML, complexity, parameters, return type, thrown exceptions, events, state,
route, tags and a body hash. An `Edge` has from, to, kind and an optional label. Ids keep their scanner prefix so
they stay stable across runs:

| Prefix | Nodes |
|---|---|
| `cs:` | C# types and members; `cs:endpoint:VERB /route` for minimal APIs |
| `vue:`, `ts:` | components; modules, functions and stores (`file#name`) |
| `route:` | frontend routes |
| `proj:`, `pkg:` | project files, NuGet/npm packages |
| `ext:` | external systems (`kind: external`, tags `[category, certainty]`) |
| `config:` | configuration keys, lowercase (`kind: config`, tags `env:<environment>@<file>`, `url:<environment>=<host>`) |

Edge kinds:

| Kind | Meaning |
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
| `uses-namespace` | type → external namespace (`ns:<name>`) its file uses: C# `using`, Java `import` (own, `System.*`, `java.*` dropped) |

`CodeModel.DependencyKinds` lists the edge kinds that count as a dependency for the architecture rules. `accesses`,
`connects`, `reads` and `binds` are left out on purpose, so adding them didn't change any layer rule.

### What counts as a fact

The model separates what the code states from what DocWizz concludes:

- **Facts** come from syntax and semantics: symbols, signatures, calls, routes, `[Authorize]`, `DbSet<T>`.
- **Certainty** is recorded where detection isn't exact. An external system is `detected` when a call shows it
  (`UseNpgsql`, `AddHttpClient<T>` with a base address, `fetch('https://…')`). It is `inferred` when only a package
  reference or a single candidate points to it. It is `unknown` when something is there but the code doesn't say
  what, e.g. a DbContext with no provider.
- **Name heuristics** are marked in code with `ponytail:` comments and only produce tags: `*Service` → service,
  `*Repository` → repository. An external system is never invented from a name.
- **Values** from configuration and deployment files are never stored, because they may be secrets. The one
  exception is the host of a URL.

## Scanners

- **[CSharpScanner](src/DocWizz/CSharpScanner.cs)** uses Roslyn with BCL references only. ASP.NET, EF Core and other
  package types stay unresolved and are matched by name. It covers types, members, doc comments (with `comment_docs`, also the `//` block above a member), complexity, calls,
  injection, `accesses`, events, controllers and minimal APIs (`MapGroup` prefixes, group authorization, parameter
  binding inferred as ASP.NET does when there is no `[From*]`), response codes and types (`ProducesResponseType`,
  `.Produces<T>()`, `Results<…>`, `Ok(x)`/`TypedResults.X` in the body), the middleware pipeline in registration order,
  DI registrations, `AddHostedService<T>`, EF `DbSet`, external-system calls and configuration reads.
- **[scanner-vue](scanner-vue/index.mjs)** uses the TypeScript compiler and `@vue/compiler-sfc`, run by
  [Frontend.cs](src/DocWizz/Frontend.cs) as a Node child process. It covers SFC props, emits, state, lifecycle hooks,
  composables and template renders, exported functions, stores, classes (with their methods), interfaces, types and
  enums, imports and calls, and router routes. HTTP calls are `fetch`/`axios`, and calls through wrappers: an
  `axios.create({ baseURL })` instance, or a helper that passes its parameters on as URL and method (`request('POST',
  url)`). A pre-pass finds the wrappers in every file, so they resolve across imports. Plain `.js`/`.mjs` is read like TypeScript (`*.min.js` and `wwwroot/lib/` are skipped). React (`.tsx`/`.jsx`): capitalised
  functions returning JSX are components (props, hooks, rendered children), routes from `createBrowserRouter` and
  `<Route>`. Angular: `@Component` classes (`@Input`/`@Output`, signal inputs, lifecycle methods, children by template
  selector), `@Injectable` services, `HttpClient` calls, calls through injected services, `Routes` incl. `loadComponent`.
- **[JavaScanner](src/DocWizz/JavaScanner.cs)** reads Java without a parser (comments and strings masked, then
  regexes): types and members with Javadoc, Spring roles, `@*Mapping` endpoints with parameter sources, auth and the
  unwrapped return type, constructor/`@Autowired`/Lombok injection, calls through injected fields (overloads by name
  and argument count), method-level `implements`, `@Value`/`@ConfigurationProperties` reads, and imported packages. Projects come from
  `pom.xml`/`build.gradle`, configuration from `application*.yml|properties`.
- **[Sql](src/DocWizz/Sql.cs)** reads `.sql` files: procedures, functions, views, triggers and tables (doc from the
  comment above, `@parameters`), the tables each routine touches and the procedures it EXECs, and migration files
  (a `migrations/` folder or Flyway names). C# literals that EXEC a procedure, or name it next to
  `CommandType.StoredProcedure`, link the calling member to it; EF `Migration` classes are tagged `migration`.
- **[Projects](src/DocWizz/Projects.cs)** reads `.csproj` (SDK, target frameworks, package and project references,
  role: executable, library or test from `OutputType`, the SDK and test packages), `package.json`, and the solution
  folders of `.sln`/`.slnx`. Files in a test project count as test code, whatever their path.
- **[Configuration](src/DocWizz/Configuration.cs)** reads `appsettings*.json`, `.env`, Kubernetes ConfigMaps and the env
  blocks of Helm `values*.yaml`: keys per environment, no values (only the host of a URL).
- **[Externals](src/DocWizz/Externals.cs)** holds the curated list of known systems (packages, calls, container
  images, IaC resource types) and the link step: absolute URLs, package-only evidence, and DbContexts without a known database.

## Analysis

- **[Analyzer](src/DocWizz/Analyzer.cs)** decides per symbol whether it needs documentation. That decision uses
  visibility, complexity, parameters, fan-in, side effects and the profile's pattern level. It then records which
  sections the profile requires and where each present one comes from: `written` (doc comment), `fact` (derived
  from the model), `inferred` (side effects) or `ai`. AI drafts never close a gap. Written docs also get quality
  flags: facts where they contradict the model (a `<param>` for a parameter that doesn't exist), inferences where a
  heuristic says they add nothing. Doc quality % is the share of items with written docs and no flags.
- **[Profiles](src/DocWizz/Profiles.cs)** are YAML documentation patterns: match on kind, name, type, tag or
  visibility, then list the sections required. Reports say "coverage against profile X", never "compliant with".
- **[Architecture](src/DocWizz/Architecture.cs)** covers path-glob layers, allowed dependencies, violations
  ARCH-001/002/004 with severities, and folder cycles (ARCH-003). User-defined rules (`architecture.rules`) run over
  the same edges: a selector for the source (layer, path, kind, tag, name; tags and kinds also match through the
  containing type) and what it must not reach (a target selector, a package prefix or edge kinds). They are validated
  when the config loads and produce the same `Violation`s, marked `Custom`, so `check`, `--since` and the pages need
  nothing extra. `architecture.md` adds coupling per module (fan-in,
  fan-out, instability) and risks that break no rule: entities returned by endpoints, complex members in the API layer,
  and external namespaces used by the domain layer.
- **[Security](src/DocWizz/Security.cs)** (opt-in) turns facts the model already has into findings for a security
  review: endpoint auth tags, DbContext access from the API layer, framework namespaces in the domain, and flows from
  unprotected endpoints to sensitive external systems (the generator's flow trace). A finding is a `Violation` with a
  basis (fact or inferred detection), a CIA concern and the risk, kept apart from the detected fact. `Architecture.Check`
  appends them when enabled, so `check`, `--since` and the pages treat them like violations but show them separately.
- **[Diff](src/DocWizz/Diff.cs)** compares two models by symbol id and body hash. It reports changed, added and
  removed symbols, the affected pages, and the gaps and violations a change introduced. A touched symbol also marks
  every flow passing through it: `api.md` / `frontend.md` and the module pages along that flow. New external systems
  and new layer dependencies are listed as ADR candidates: decisions to record, not decisions DocWizz makes. Every added
  and changed symbol also gets its linked tests ([TestLinks](src/DocWizz/TestLinks.cs): `tests` edges to it, its
  interface, an implementation or a member — the same rule as the analyzer's "tested"), or is listed as unlinked.

## Generation

`Generator` is one partial class, split by page:

| File | Pages |
|---|---|
| [Generator.cs](src/DocWizz/Generator.cs) | `index.md`, `architecture.md`, `api.md`, `frontend.md`, `quality.md`, shared helpers |
| [Modules.cs](src/DocWizz/Modules.cs) | `modules/<folder>.md`: role, key components, API, data, external systems, configuration, flows, gaps, observations, then the component reference |
| [Flows.cs](src/DocWizz/Flows.cs) | flow tracing used by api.md, frontend.md and module pages |
| [Views.cs](src/DocWizz/Views.cs) | `views/context.md`, `containers.md`, `components.md`, `data.md`; the configuration table |
| [Deployment.cs](src/DocWizz/Deployment.cs) | `views/deployment.md`: compose, Kubernetes, Dockerfiles, IaC, from the scan's file list (`exclude:`, `.gitignore`) |
| [Description.cs](src/DocWizz/Description.cs) | `architecture-description.md`, structured after ISO/IEC/IEEE 42010 |

**Flows.** A flow is a 0-1 breadth-first search from an endpoint or route. It follows `calls`, `accesses`, `http`,
`renders`, `routes-to`, `connects` and minimal-API `injects`, and dispatches interface members to their
implementations. Steps inside a unit (a C# type) and interface dispatch cost 0, so the hops shown are between
units. Frontend flows stop at endpoints, because api.md continues from there.

**Output rules.** Sections appear only when there is content. Diagrams are capped (`MaxDiagramEdges`,
`MaxViewNodes`) or replaced by a note. Output is deterministic without `--ai`.

**Safe regeneration.** Every generated page starts with a marker line. Only pages with that marker are deleted or
overwritten. `docs/architecture/*.md` and anything else written by hand is linked, never touched.

## AI

With `--html`, [Html](src/DocWizz/Html.cs) renders every page with Markdig next to its Markdown twin, so relative
links to sources still work; links between generated pages point at the HTML, Mermaid renders client-side, and a
search box reads the index from `search.js`.

[AiProse](src/DocWizz/AiProse.cs) drafts documentation for items that still lack a summary (summary, responsibilities,
behaviour, side effects, errors, usage) and an overview per module. The model answers in JSON, one list of sentences
per section, each citing the facts it uses; citations are resolved to symbol ids and a sentence without a valid one is
dropped (per-sentence provenance). Per item it sends the facts JSON
(graph neighbours, signature, derived sections with their origin) and that symbol's own source lines. It never
sends the repository, and it only talks to a self-hosted Ollama: every connection's resolved address must be
loopback or private, proxies are bypassed and Ollama cloud models are refused, so code never reaches a public
service. Drafts are cached per symbol and body hash in `docs/.docwizz/ai-cache.json`, together with
the symbols they were drafted from (provenance). Drafts are marked 🤖 and never override written documentation.
With the same facts and the doc comment, it also rates written docs (score 1–5, missing gaps, a note), cached per
symbol, doc and body hash in `ai-assessments.json`; the rating is shown in `quality.md` only and never feeds doc
quality % or `check`.

## Tests

Three layers, so a failure points at the part that broke. CI ([test.yml](.github/workflows/test.yml)) runs each as
its own step.

| Layer | Where | What it covers | Run |
|---|---|---|---|
| Unit | [tests/DocWizz.Tests/Unit](tests/DocWizz.Tests/Unit/) | `Analyzer`, `Architecture`, `Diff`, `CodeModel` on small in-memory models | `dotnet test --project tests/DocWizz.Tests --filter-namespace DocWizz.Tests.Unit` |
| Component | [tests/DocWizz.Tests/Component](tests/DocWizz.Tests/Component/) | one scanner on a few source snippets → nodes and edges, incl. past regressions per backend language | `… --filter-namespace DocWizz.Tests.Component` |
| End-to-end | [test.sh](test.sh) | repository → model → reports → generated pages, on the fixtures | `./test.sh` |

`test.sh` runs the CLI against [fixture/](fixture/), a small ASP.NET + EF Core + Vue project with
deliberate gaps, violations, external systems, configuration and deployment descriptors. It asserts facts in the
model JSON and lines in the generated pages. When a feature is added, extend the fixture with the smallest case
that exercises it, and assert both the model and the page. Rules and analysis logic get a unit test as well, and a
scanner fix gets a component test with the snippet that broke it.

Performance is measured separately: [bench/](bench/README.md) generates synthetic repositories of three sizes and
times every stage (`--timings`, [Timings.cs](src/DocWizz/Timings.cs)) against a committed baseline, on demand and weekly.
