# docwizz CLI reference

[Documentation home](README.md) · [Getting started](getting-started.md) · [Configuration](configuration.md)

Syntax, options, precedence rules and exit codes for every command. New here? Run `docwizz setup .` and read what it
prints. Walkthroughs are in the [guides](README.md).

`docwizz help` lists the commands. `docwizz help <command>` or `docwizz <command> --help` shows one command's purpose,
an example and its options. `<dir>` defaults to `.` everywhere. Running `docwizz` with no command prints the
recommended next step.

## Commands

| Command | Syntax | Purpose | Use |
|---|---|---|---|
| `setup` | `docwizz setup [dir] [--select-projects] [--all-projects] [--yes] [--force] [--html] [--progress] [--refresh-html-on-version-change] [--ai] [--ai-ratings] [--profile p]` | detect the stack, write `docwizz.yaml`, analyze, architecture, generate, check | start here |
| `analyze` | `docwizz analyze <dir> [--format f] [--profile p]` | documentation and architecture report, no gate | everyday |
| `architecture` | `docwizz architecture <dir> [--format f] [--profile p]` | layers, dependencies, violations, cycles; exit 1 above thresholds | architects |
| `generate` | `docwizz generate <dir> [out] [--html] [--screenshots --base-url <url> [--storage-state <file>] [--screenshot-delay <ms>]] [--journeys <dir>] [--progress] [--refresh-html-on-version-change] [--ai] [--ai-ratings] [--requirements] [--ado-project <name> --ado-area <path> [--history-since <ref>]] [--profile p]` | write the docs (default `<dir>/docs`) | everyday |
| `check` | `docwizz check <dir> [--since ref] [--format f] [--profile p]` | quality gate; exit 1 when thresholds fail | CI |
| `diff` | `docwizz diff [dir] [base] [head] [--format f] [--profile p]` | change impact vs the last `generate` or git refs | reviews, CI |
| `discover` | `docwizz discover [dir] [--format console\|json]` | read-only inventory of solutions, projects and references | everyday |
| `context` | `docwizz context <dir> --for target [--hops n] [--budget tokens] [--format md\|json] [--include-ai] [--requirements]` | token-budgeted context package for one symbol, file, folder or endpoint | coding agents |
| `mcp` | `docwizz mcp <dir> [--auto-rescan]` | read-only MCP server on stdio: find_symbol, context, callers, impact, check, … | coding agents |
| `domain` | `docwizz domain discover <dir> [out] (--seed <symbol\|endpoint> \| --work-item <id>) [--depth n] [--limit n] [--format md\|json]`; `docwizz domain explain <dir> [out] (--seed … \| --work-item …) [--depth n] [--limit n] [--ai] [--force] [--format md\|json]`; `docwizz domain areas <dir> [--format json]` | follow code and linked requirements from one start point; explain: a reviewable draft of the flow; areas: inferred candidate business areas | architects |
| `requirements` | `docwizz requirements import <dir> <history.json> [<work-items>] [--progress]`, `docwizz requirements catalog-import <dir> <catalog.json>`, `docwizz requirements show <dir> (--for <symbol> \| --work-item <id>) [--format json]`, `docwizz requirements query <dir> <query.json>` | index exported history; one symbol's requirements, or one work item's symbols; gitwizz's requirement query | coding agents, reviews |
| `tests` | `docwizz tests import <dir> <snapshot.json>` | validate and store offline ADO test traceability | product documents |
| `trace` | `docwizz trace <dir> <evidence.yaml> [--format json]` | explicit traceability and missing links | product documents |
| `evidence-diff` | `docwizz evidence-diff <dir> <before.yaml> <after.yaml> [--format json]` | explicit evidence changes and affected tests | product documents |
| `product` | `docwizz product [check or publish] <dir> <template.yaml> [out.md or out-dir] [--context file.yaml] [--control document-control.yaml] [--approval signed.json] [--trust policy.json] [--draft existing.md] [--ai] [--format f]` | product draft from evidence; `check`: references and freshness; `publish`: signed DOCX/PDF | architects |
| `load` | `docwizz load import <dir> <report.json>...` | store powerwizz reports; `generate` adds measured numbers per endpoint to `api.md` | performance |
| `sbom` | `docwizz sbom [dir] [out]` | direct declared dependencies as CycloneDX 1.6 JSON | supply-chain inventory |
| `remediate` | `docwizz remediate <dir> [--package n --to v] [--validate] [--since ref] [--format f]` | package update suggestions: command or patch, impact, confidence | maintenance |
| `edmx` | `docwizz edmx <dir> <file.edmx> [--graph] [--links] [--degree-weight n] [--fan-in-weight n] [--fan-out-weight n] [--inheritance-weight n] [--format console\|json]` | read-only EF6 EDMX facts, technical graph (`--graph`) or C# code links (`--links`) | architects |
| `ai` | `docwizz ai check [dir] [--format console\|json]` | test the configured local model with a synthetic request, no repository code | diagnostics |
| `init` | `docwizz init [dir]` | write a `docwizz.yaml` with every default; refuses to overwrite | manual setup |
| `scan` | `docwizz scan <dir> [model.json]` | dump the raw code model | advanced |
| `help` | `docwizz help [command]` | all commands, or one | — |

## Options

| Option | Default | Applies to | Interaction | Use |
|---|---|---|---|---|
| `--profile <name\|file.yaml>` | `profile:` in `docwizz.yaml`, else `default` | setup, generate, check, analyze, architecture, diff | overrides `profile:` and the file's own `patterns:` | normal |
| `--format console\|json` | `console` | ai, discover, edmx, analyze, check, architecture, context, domain, diff, trace, evidence-diff, product, remediate, requirements | `json` prints one JSON document to stdout; `context` also takes `md` (the same as `console`) | CI, tooling |
| `--graph` | off | edmx | the versioned `docwizz.edmx-graph/v1` technical graph | architects |
| `--links` | off | edmx | exact C# code links and inferred domain-area hints (`docwizz.edmx-code-links/v1`); incompatible with `--graph` | architects |
| `--degree-weight <n>` | 1 | edmx | 0–100; only with `--graph`; multiplies resolved association degree | architects |
| `--fan-in-weight <n>` | 1 | edmx | 0–100; only with `--graph`; multiplies incoming explicit navigations | architects |
| `--fan-out-weight <n>` | 1 | edmx | 0–100; only with `--graph`; multiplies outgoing explicit navigations | architects |
| `--inheritance-weight <n>` | 1 | edmx | 0–100; only with `--graph`; multiplies resolved inheritance links; at least one weight must be positive | architects |
| `--html` | off | generate, setup | adds HTML pages; the Markdown is written either way | normal |
| `--progress` | interactive terminals | generate, setup, requirements | forces progress output on stderr when output is redirected | normal |
| `--refresh-html-on-version-change` | off | generate, setup | implies `--html`; rewrites all HTML when the docwizz version changed | normal |
| `--screenshots --base-url <url>` | off | generate | captures detected static routes from a running frontend ([guide](guides/screenshots.md)) | opt-in |
| `--storage-state <file>` | off | generate | with `--screenshots` or `--journeys`, starts from a saved Playwright login state; path relative to the scanned directory; keep it out of Git | opt-in |
| `--screenshot-delay <ms>` | 0 | generate | with `--screenshots` or `--journeys`, extra wait (0 to 60000 ms) after the page settles | opt-in |
| `--journeys <dir>` | off | generate | user guides: plays journey files (`*.yaml`, needs `--base-url`) and imports passed TestWizz evidence ([guide](guides/screenshots.md#user-guides)) | opt-in |
| `--ai` | off | generate, product, setup, domain | self-hosted model only ([AI](configuration.md#ai)); cached drafts are used even without it; does not enable ratings. `domain`: only `domain explain` | opt-in |
| `--ai-ratings` | off | generate, setup | rates written docs; cached ratings are used without it; combine with `--ai` for drafts too | opt-in |
| `--since <ref>` | — | check, remediate | check: only problems introduced since `<ref>` fail; remediate: says whether each update touches only what changed since `<ref>` | CI |
| `--for <target>` | — | context, requirements | required for context; a symbol id or qualified name, a file, a folder, or `VERB /route`; ambiguous or unknown exits 1 with candidates. `requirements show` takes a symbol, or an id the index knows from history | coding agents |
| `--work-item <id>` | — | requirements, domain | `requirements show`: reverse lookup; `domain`: start from the work item's linked symbols instead of `--seed`; `github#123` selects a GitHub issue, numeric ids mean Azure Boards | architects, reviews |
| `--seed <symbol\|endpoint>` | — | domain | one exact or qualified symbol, or `VERB /route`; incompatible with `--work-item` | architects |
| `--depth <n>` | 3 | domain | traversal hops, 0–8; 0 means only the start symbols | architects |
| `--limit <n>` | 100 | domain | maximum reached symbols, 1–500; truncation is reported | architects |
| `--hops <n>` | 2 | context | how far neighbours are listed, 1 to 4; hops beyond 1 are signature only | coding agents |
| `--budget <tokens>` | 6000 | context | at least 100; estimated as characters / 4; lower-priority sections are cut first, and the cut is reported | coding agents |
| `--ado-project <name>` | — | generate | export and import ADO evidence before generating ([guide](guides/requirements.md#exporting-from-azure-devops-in-one-step)); requires `--ado-area` | opt-in |
| `--ado-area <path>` | — | generate | Azure DevOps area path; requires `--ado-project` | opt-in |
| `--history-since <ref>` | — | generate | with `--ado-project`, also exports GitWizz code history after `<ref>` | opt-in |
| `--requirements` | off | generate, context | generate: AI prompts also get each symbol's linked work items; context: adds a Requirements section. Needs `docwizz requirements import`; exits 1 without an index | opt-in |
| `--include-ai` | off | context | includes cached 🤖 drafts, marked `ai-drafted`; never calls a model | coding agents |
| `--auto-rescan` | off | mcp | rescans when the model is stale instead of only reporting `stale: true` | coding agents |
| `--package <name> --to <version>` | `remediation.targets` and version drift | remediate | given together or not at all | maintenance |
| `--validate` | off | remediate | runs the configured restore/build/test commands in a temporary copy, never the working tree | maintenance |
| `--context <file.yaml>` | off | product | sourced project statements, validated against the template before writing | product documents |
| `--control <file.yaml>` | off | product | adds cover, revision history, review references and clickable contents; not an approval process | product documents |
| `--approval <signed.json>` | off | product | independently signed quality and security approvals; only `product publish` | product documents |
| `--trust <policy.json>` | off | product | reviewed public-key trust policy; only `product publish` | product documents |
| `--draft <existing.md>` | off | product | the docwizz-generated draft to publish, inside the repository (default `docs/products/<id>.md`); only `product publish` | product documents |
| `--force` | off | setup, domain | `setup`: replaces an existing `docwizz.yaml`; `domain explain`: replaces an output file docwizz didn't write | explicit |
| `--select-projects` | off | setup | terminal-only project selection after discovery; on an existing config replaces only `scope:` | setup only |
| `--all-projects` | off | setup | removes a saved project scope while keeping other YAML | setup only |
| `--yes` | off | setup | never prompts; does not change an existing scope | setup only |
| `--timings` | off | all | time and peak memory per stage, on stderr | benchmarks |
| `--help`, `-h` | — | all | prints the command's help and exits | — |
| `--version`, `-v` | — | all | prints `docwizz <version> (commit <sha>, .NET <runtime>, <platform>)` and exits 0, whatever the command; `docwizz version` does the same | bug reports, CI logs |

## Environment variables

| Variable | Effect |
|---|---|
| `OLLAMA_HOST`, `DOCWIZZ_MODEL` | Ollama server (default `localhost:11434`) and model (default `qwen2.5-coder:7b`) for `--ai` |
| `DOCWIZZ_SCANNER_TIMEOUT_SECONDS` | time limit per Node scanner run (default 600); a scanner that takes longer is stopped and its language skipped with a warning |
| `DOCWIZZ_SCANNER_VUE` | path to another `scanner-vue/index.mjs` |
| `DOCWIZZ_GITWIZZ_DLL` | GitWizz assembly for `--history-since`, when `gw` is a shell alias ([guide](guides/requirements.md#exporting-from-azure-devops-in-one-step)) |
| `DOCWIZZ_DEBUG=1` | prints the full stack trace when docwizz fails unexpectedly |
| `NO_COLOR` | turns colors off |
| `CI` | when set, `setup` never prompts |

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
| `domain` | report written, also without a requirements index; `explain` also when the model fails (deterministic draft and a warning) | missing or ambiguous seed, invalid options, no index for a work-item seed, or an output file docwizz didn't write (`explain` without `--force`) |
| `requirements` | `import`: index written or unchanged; `show`: answer on stdout (any outcome, `no-link` included); `query`: answer on stdout (a missing index included) | missing or invalid input, unknown schema, no index (`show`), unknown target, an unreadable query, bad arguments |
| `tests` | snapshot validated, saved and summarized | missing or invalid snapshot or unsupported version |
| `trace` | report on stdout | invalid or missing catalog; no data written |
| `evidence-diff` | report on stdout | invalid or missing catalogs; no data written |
| `product` | draft written or unchanged; `check`: no errors (warnings allowed) | invalid template, unsafe overwrite, bad arguments; `check`: an error finding; `publish`: any failed precondition |
| `load` | reports stored (unmatched or ambiguous steps are listed on stderr) | no report, a file that isn't `powerwizz.report/v1`, invalid JSON, a missing or mistyped field, a broken `load.json` |
| `remediate` | done (suggestions alone never fail) | a `--validate` run failed, or bad arguments |
| `edmx` | report printed without error findings | invalid or missing input, invalid graph weights, or an EDMX error finding |
| `ai` | the configured model answered a synthetic request | model or endpoint unavailable, disallowed, or an invalid answer |
| `init` | written | `docwizz.yaml` exists |
| no command | — | prints how to start |
| `--version`, `version` | prints the version | — |

Every command exits **2** when docwizz itself fails unexpectedly (a bug, a full disk, a permission error). It prints one
line instead of a stack trace, so CI can tell "docwizz broke" from "the gate failed". Set `DOCWIZZ_DEBUG=1` for the
full trace. A Node scanner (Java, Vue, React, Angular, TypeScript) that fails shows the last lines it wrote to stderr in
its warning.

## Generated vs. your files

| Path | Who owns it |
|---|---|
| `docwizz.yaml` | **you**. `setup`/`init` create it once and then leave it alone (`setup --force` replaces it). |
| `docs/*.md`, `docs/solutions/`, `docs/projects/`, `docs/modules/`, `docs/views/`, `docs/routes/`, `docs/requirements/`, `docs/user-guides/`, `*.html` | docwizz. Regenerated, so don't edit them. |
| `docs/products/*.md`, `*.evidence.json` | docwizz `product`: the draft and its evidence report; untouched by `generate`. |
| `docs/architecture/*.md` | **you**. Linked from the generated pages and never overwritten. |
| `docs/.docwizz/model.json`, `documentation.json` | docwizz: the baseline `diff` compares against, and the documentation model as data. |
| `docs/.docwizz/requirements.json` | docwizz `requirements import`: contains work-item titles and criteria, which `generate` publishes in `docs/requirements/`. |
| `docs/.docwizz/ai-cache.json`, `ai-assessments.json` | docwizz: AI drafts and ratings, cached by code hash. Commit them to avoid re-sending code. |
| `docs/.docwizz/domain-ai-cache.json` | docwizz `domain explain --ai`: the model's raw answers, by prompt and model; validated again on every run. |

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
build derives it from the tags in its checkout ([DocWizz.csproj](../src/DocWizz/DocWizz.csproj)):

| Built from | `docwizz --version` |
|---|---|
| a release tag (`v0.2.2`) | `0.2.2` |
| 4 commits after `v0.2.2` | `0.2.3-dev.4`: a pre-release of the next patch |
| no git, or no tags (a shallow clone, a source archive) | `0.0.0-dev` |

`git fetch --tags` updates the tags of an existing clone.

### Releases

Every pull request merged into `main` is a release. The [release workflow](../.github/workflows/release.yml) bumps the
latest `vX.Y.Z` tag according to the PR's label, builds and tests that version, tags the merge commit and creates a
GitHub release with generated notes. Nothing is committed to `main`.

| PR label | Release |
|---|---|
| none | patch: `0.1.0` → `0.1.1` |
| `release:minor` | minor: `0.1.3` → `0.2.0` (new features) |
| `release:major` | major: `0.4.2` → `1.0.0` (breaking changes) |
| `release:none` | nothing (docs, CI); the next merge without it releases |

Two release labels on one PR fail the workflow instead of guessing, and a commit already covered by a newer tag is not
released again. The bump rule is [semver-next.sh](../.github/scripts/semver-next.sh), specified by
[semver-next.test.sh](../.github/scripts/semver-next.test.sh).
