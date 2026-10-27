# Getting started

[Documentation home](README.md) · [CLI commands and flags](cli.md)

Run `docwizz setup .`, then open the generated `docs/index.md` or `docs/index.html`. The full initial workflow and status contract follows.

## Install as a .NET tool

From the cloned repository root, with .NET 10 SDK and Node.js installed:

```bash
npm ci --prefix scanner-vue
dotnet pack src/DocWizz -c Release -o artifacts
dotnet tool install --global DocWizz --add-source ./artifacts
docwizz --help
```

The DocWizz package bundles the Vue/Java scanners; `npm ci` prepares their dependencies before packing. If already installed, use `dotnet tool update --global DocWizz --add-source ./artifacts`.
Ensure `$HOME/.dotnet/tools` is on your `PATH`. These are local packages; no NuGet publication is required.

## First setup

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

**What `setup` never does:** contact an AI endpoint without `--ai` or `--ai-ratings`; run the repository's build, tests or package tools
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

## Next steps

- [Generate documentation and optionally draft prose](guides/generation.md)
- [Quality gates and diff workflows](guides/quality.md)
- [Import requirements and test evidence](guides/requirements.md)
- [Agent context and MCP](guides/agent-context.md)
