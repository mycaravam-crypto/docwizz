# Configuration

[Documentation home](README.md) · [CLI reference](cli.md)

docwizz reads `docwizz.yaml` from the scanned directory, then from the current one, else uses built-in defaults.
`docwizz setup` writes one from what the repository shows; `docwizz init` writes the plain defaults with comments.
This repository's own [docwizz.yaml](../docwizz.yaml) is a working example.

```yaml
profile: aspnet                     # what needs docs: see Profiles
check:
  min_coverage: 80                  # % of items that need docs and have them
  max_critical: 0
  max_violations: 0
  max_cycles: 0
  fail_on: low                      # lowest violation severity that counts: low, medium, high
  min_quality: 90                   # optional: % of written docs with no quality flag
  max_complexity: 20                # optional: fail on any symbol above this cyclomatic complexity
  require_tests: high               # optional, with --since: fail on new symbols at this level no test links to
architecture:
  layers:                           # path globs, first match wins; unmatched files have no layer
    api: ["*/Api/*.cs", "*/Controllers/*.cs"]
    domain: ["*/Domain/*.cs"]
  allow:                            # who may depend on whom; `http` = calling HTTP directly
    api: [application, domain, infrastructure]
    domain: []
  severity: { ARCH-002: high }      # override a rule's default severity
tests: ["tests/*", "*.Tests/*"]     # test code: only used to link tests to the code they exercise
exclude: ["legacy/*"]               # left out of the model entirely
comment_docs: true                  # count a // comment block above a C# member as its summary (default false)
```

The other top-level keys are `security` ([Security rules](#security-rules)), `remediation` ([Remediation](#remediation)),
`ai` ([AI](#ai)) and `scope` ([multi-project repositories](guides/multi-project.md#scope)). Unknown keys and invalid
values stop docwizz with a message naming them.

## Profiles

A profile decides which items need docs and which sections they need: `summary`, `param`, `returns`, … or derived
ones such as `endpoint`, `authorization`, `side_effects`. `--profile` overrides `profile:`.

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

Use your own with `--profile team.yaml` or `profile: team.yaml`, in the shape of
[Profiles.cs](../src/DocWizz/Model/Profiles.cs); a `patterns:` block in `docwizz.yaml` replaces the profile's patterns.
Coverage is measured against the profile; docwizz never claims compliance with a standard.

Besides XML doc and JSDoc, a plain comment counts above a minimal-API `Map*` call, before a Vue component's first block
or function, and above a Vue prop in `defineProps`.

## Doc quality

Coverage says a section exists; quality says whether it can be trusted. `check.min_quality` gates the share of written
docs with no flag.

| Flag | Basis | Fires when |
|---|---|---|
| `param-drift` | fact | a `<param name>` that isn't one of the parameters |
| `returns-on-void` | fact | `<returns>` on a method that returns nothing (`void`, `Task`) |
| `empty-inheritdoc` | fact | `<inheritdoc/>` with nothing to inherit; counts as no docs |
| `placeholder` | inferred | TODO/FIXME/TBD, template boilerplate, or under three words |
| `echo` | inferred | the summary only repeats the name, types or parameters ("Gets the stock level" on `GetStockLevel`) |

## Architecture rules

| Rule | Fires when | Default severity |
|---|---|---|
| ARCH-001 | a layer depends on one it may not (`allow`) | high |
| ARCH-002 | UI code calls HTTP directly instead of through a client | medium |
| ARCH-003 | two modules depend on each other (cycle) | counts against `max_cycles` |
| ARCH-004 | a forbidden dependency that skips an allowed layer | low |

`setup` keeps only the default layers some file falls into; review them and `allow` so they state your intent rather
than the defaults. Generated pages also list *risks* that break no rule: entities returned from the API, business logic
in controllers, domain code bound to external packages.

### Custom rules

A custom rule says what code (`from`) must not reach (`forbid`), with optional exceptions:

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
match through the containing type. `edge` takes edge kinds from [the code model](internals/architecture.md#the-code-model).
Custom findings count for `architecture` and `check` like built-in ones; an invalid rule stops docwizz with a message.

## Security rules

Off by default; turn them on with `security: { enabled: true }`. Findings support a security review; they are not an
ISO/IEC 27001 assessment.

| Rule | Fires when | Default severity |
|---|---|---|
| SEC-001 | a read endpoint declares neither authorization nor anonymous access | medium |
| SEC-002 | a mutating endpoint doesn't require authorization | high |
| SEC-003 | API-layer code uses a DbContext | medium |
| SEC-004 | domain code imports a security or web framework | medium |
| SEC-005 | an unauthenticated endpoint can reach a sensitive external system (`security.sensitive`, default: database, storage, identity, email, messaging, cache) | high |

Authorization is read from ASP.NET (`[Authorize]`, `RequireAuthorization()`, …) and Spring (`@PreAuthorize`,
`@Secured`, …) endpoint metadata. Policies configured elsewhere, such as a fallback policy or a filter chain, are
invisible, so each finding says "unless".

## Remediation

Settings for [`docwizz remediate`](guides/dependencies.md#package-updates):

```yaml
remediation:
  targets: { Newtonsoft.Json: 13.0.3 }   # versions to propose
  validate:                              # --validate steps; default: restore, build, test with dotnet
    - { name: restore, run: "dotnet restore {target}" }   # {target}: the solution, else each affected project
    - { name: build, run: "dotnet build {target} --no-restore -warnaserror" }
    - { name: test, run: "dotnet test {tests} --no-build" }   # {tests}: the solution, else each affected test project
  timeout_minutes: 20                    # per step
```

## AI

AI is used only with `--ai` or `--ai-ratings`, and only with a self-hosted model: every resolved address must be
loopback or private, proxies are bypassed and Ollama `…-cloud` models are refused. `docwizz ai check .` tests the
configuration with one synthetic request that contains no repository code.

**Ollama** (the default): `OLLAMA_HOST` (default `localhost:11434`) and `DOCWIZZ_MODEL` (default `qwen2.5-coder:7b`)
choose the server and model. docwizz sends four requests at once; set `OLLAMA_NUM_PARALLEL=4` on the server so it runs
them together, which roughly doubles drafting speed on one GPU. Each request asks for a 16,384-token context
(`num_ctx`) so long prompts aren't cut at Ollama's smaller default; four such contexts may not fit a small GPU, so lower
`OLLAMA_NUM_PARALLEL` there.

**OpenAI-compatible servers** (vLLM, NVIDIA NIM, LiteLLM, …):

```yaml
ai:
  provider: openai-compatible   # ollama (default) or openai-compatible
  endpoint: http://10.0.0.5:8000/v1
  model: qwen3-coder-next
  api_key_env: DOCWIZZ_AI_KEY   # optional: the variable that holds the key
  requirements: { max: 3, budget: 1200 }   # with --requirements: work items per symbol, characters of their text
```

The key is read only from the variable `api_key_env` names; an `api_key` setting is refused and the key is never
printed. If the variable is named but empty, `--ai` is skipped with a message. `DOCWIZZ_MODEL` overrides `model`; for
Ollama, `OLLAMA_HOST` overrides `endpoint`. What the model is asked and how its answers are checked:
[AI drafts](guides/generation.md#ai-drafts).
