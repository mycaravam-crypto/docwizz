# docwizz CLI reference

New here? Run `docwizz setup .` and read what it prints. You only need this page when you want something specific.

- [Workflows by goal](#workflows-by-goal): what to run for the thing you want to do
- [Commands](#commands) and [options](#options): every command and flag
- [Precedence and conflicts](#precedence-and-conflicts): what wins when settings overlap
- [Exit codes](#exit-codes)
- [Versioning](#versioning): what `docwizz --version` prints and how releases are numbered
- [Generated vs. your files](#generated-vs-your-files)

`docwizz help` lists the commands. `docwizz help <command>` or `docwizz <command> --help` shows one command's purpose,
an example and its options.

## At a glance

| Command / option | Primary use | Typical user | Default path? |
|---|---|---|---|
| `setup` | first repository setup | all users | **yes, start here** |
| `generate` | write or refresh documentation | developers | yes |
| `check` | quality gate | CI, developers | yes |
| `analyze` | documentation report | developers | yes |
| `architecture` | architecture diagnostics | architects | optional |
| `diff` | change impact | developers, CI | optional |
| `sbom` | CycloneDX manifest SBOM | supply-chain inventory | optional |
| `remediate` | package update suggestions | maintainers | optional |
| `init` | blank config with every default | manual setup | no |
| `scan` | raw code model | advanced, debugging | no |
| `--html` | HTML next to the Markdown | documentation readers | optional |
| `--ai` | local AI drafts | opt-in users | optional |
| `--profile` | what counts as documented | teams with their own rules | optional |
| `--format json` | machine-readable output | CI, tooling | advanced |
| `--since <ref>` | gate only what a change introduces | CI | optional |
| `--validate` | build and test remediations in a temporary copy | maintainers | optional |
| `--force` | regenerate `docwizz.yaml` during setup | setup only | no |
| `--timings` | time and memory per stage | benchmarks | advanced |
| `--version`, `-v` | which build is installed | bug reports, CI logs | optional |

## Workflows by goal

### 1. First setup

```bash
docwizz setup .
```

This runs every stage once, in this order, and prints one summary at the end:

| # | Stage | Does | If it fails |
|---|---|---|---|
| 1 | scan | reads the code and detects the stack: languages, frameworks, project files, tests, deployment files, layers | the rest are skipped |
| 2 | config | writes `docwizz.yaml`, or keeps the existing one | the rest are skipped |
| 3 | analyze | documentation coverage and critical gaps | reported; the next stages still run |
| 4 | architecture | layer violations and cycles (and security findings if they are on) | reported; the next stages still run |
| 5 | generate | writes the docs to `<dir>/docs` | reported; the next stages still run |
| 6 | check | the quality gate at the configured thresholds | reported |

`setup` never prompts, so it works the same way in a terminal and in a script. The summary starts with a status
that keeps problems in the repository apart from problems running docwizz:

| Status | Means | Exit |
|---|---|---|
| `SUCCESS` | every stage ran; the check passes and there are no architecture or security findings | 0 |
| `SUCCESS_WITH_FINDINGS` | every stage ran; the check fails or there are findings. On a new repository this is the starting point, not an error | 0 |
| `FAILED` | a stage could not run (invalid `docwizz.yaml`, unreadable files, a scanner error); its error is listed | 1 |

`setup` never contacts an AI endpoint without `--ai`. It never runs the repository's build, tests or package tools
(`dotnet`, `npm`, `mvn`, `docker`, …): it only reads files. It never copies configuration values into
`docwizz.yaml`.

**The `docwizz.yaml` it writes** starts from the defaults (`docwizz init` writes the same defaults). It changes
only what the repository shows evidence for:

- `architecture.layers`: keeps the default layers that at least one source file falls into. Each kept layer is
  commented with `# inferred: N files, e.g. <path>`. `architecture.allow` is narrowed to the same layers. If no
  layer matches, the defaults stay and a comment says to configure them.
- `tests`: keeps the default test globs that match test files and comments them as inferred. If none match, all
  the defaults stay.

Nothing about intent is inferred. The profile, `check` thresholds, allowed dependencies, `exclude`, `comment_docs` and
security stay at their defaults. The summary's *Follow-up* list says which of them to decide yourself, and suggests a
profile when the stack has one (`aspnet` for an ASP.NET Core backend, `vue` for a Vue frontend). The same repository
and docwizz version always produce the same file, with no timestamps.

**On an existing `docwizz.yaml`:** `setup` keeps it and runs every other stage with it. Pass `--force` to replace it
with a generated one. When the generated file would be identical, `setup` leaves it untouched and reports
`unchanged`. Running `setup` again is safe, because the docs are regenerated the same way `generate` does it.

Then read `docs/index.md` (or `docs/index.html` with `--html`) and the summary's *Next useful actions*.

### 2. Documentation generation

```bash
docwizz generate .              # Markdown in ./docs
docwizz generate . site --html  # Markdown and HTML in ./site
 docwizz generate . site --refresh-html-on-version-change  # re-render HTML on DocWizz upgrades
```

The optional `--refresh-html-on-version-change` implies `--html`, and rebuilds all generated HTML if `<out>/.docwizz/html-version.txt` is absent or differs from the running DocWizz version. Markdown stays incremental. Interactive progress bars go to stderr; `--progress` forces them in CI and redirected logs. Machine-readable stdout remains unchanged.

Regenerating rewrites only the pages whose content changed and prints how many changed. See
[Generated vs. your files](#generated-vs-your-files) for what you may edit.

### 3. Architecture analysis

```bash
docwizz architecture .
docwizz architecture . --format json
```

This lists the layers, the dependencies between them, violations (each `ARCH-…` rule with a severity) and module
cycles. Set the layers (path globs, first match wins) and `architecture.allow` (which layer may depend on which) in
`docwizz.yaml`. `check.fail_on` sets the lowest severity that counts against `max_violations`, and lower ones are only
reported. To write your own rules, see [README: Architecture rules](README.md#architecture-rules).

### 4. Quality gates and CI

```bash
docwizz check .                      # everything: thresholds in `check:`
docwizz check . --since origin/main  # only what the change introduces
```

Set the thresholds under `check:` (`min_coverage`, `max_critical`, `max_violations`, `max_cycles`, `fail_on`, and
optionally `min_quality`, `max_complexity`, `require_tests`). On an older codebase, `--since` lets you adopt the gate
without first fixing everything. It fails on new critical gaps, new failing violations, docs that now contradict the
code, new symbols over `max_complexity`, and, with `require_tests`, new symbols that no test code links to.

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

Any other CI system can run the command directly. The step fails when the gate fails (exit 1):

```bash
docwizz check . --since "$BASE_SHA" --format json > docwizz.json
```

To bootstrap a repository from a script, run `docwizz setup .` and commit the `docwizz.yaml` it writes.

### 5. AI-assisted documentation

```bash
docwizz generate . --ai
docwizz setup . --ai
```

Use `--ai` when you want drafts for missing summaries and module overviews to start writing from. It is off unless you
pass it, including in `setup`. The model must be a **self-hosted Ollama** on a loopback or private address: cloud
models and public hosts are refused and proxies are bypassed. Drafts are marked 🤖 and cite the facts they rest on.
They fill only empty sections and **never** close a documentation gap, raise coverage or pass a check. AI ratings of
written docs are advisory and not part of any gate. Drafts are cached by code hash, so unchanged code is not sent
again. Without `--ai`, nothing is sent. See [README: AI drafts](README.md#ai-drafts).

### 6. Diff and impact analysis

```bash
docwizz diff .                # vs the docs last generated (docs/.docwizz/model.json)
docwizz diff . origin/main    # vs a git ref
docwizz diff HEAD~1 HEAD      # between two refs (directory defaults to .)
```

The output lists the changed, added and removed symbols, the doc pages they affect, docs that may now be stale, new
gaps and violations, and the test code linked to each changed symbol. A test link means test code *uses* the symbol.
It is not coverage.

### 7. Raw model (advanced)

```bash
docwizz scan . model.json
```

This writes every node and edge the scanners found. It is useful when debugging why a symbol is or isn't reported,
or for building your own tooling on top. Everyday workflows don't need it.

## SBOM inventory

`docwizz sbom .` writes `sbom.cdx.json` (or use `docwizz sbom . output.json`). The exporter reads NuGet, npm, Maven and Gradle **manifests only**. It records direct declared dependencies with a package URL (`purl`) and their version literals or expressions, not resolved or installed packages; ranges, wildcards and property references are kept as declared and left out of `version` and the purl. Project-to-project references appear in the dependency graph. Transitive dependencies, lockfiles, licenses and vulnerability analysis are intentionally outside the MVP. No restore, build or network access occurs. Output is deterministic for identical inputs. `docwizz generate` writes the same inventory as `views/packages.md` for readers, listing packages declared at more than one version first.

## Commands

| Command | Syntax | Purpose | Use |
|---|---|---|---|
| `setup` | `docwizz setup [dir] [--force] [--html] [--progress] [--refresh-html-on-version-change] [--ai] [--profile p]` | detect the stack, write `docwizz.yaml`, analyze, architecture, generate, check | start here |
| `generate` | `docwizz generate <dir> [out] [--html] [--progress] [--refresh-html-on-version-change] [--ai] [--profile p]` | write the docs (default `<dir>/docs`) | everyday |
| `check` | `docwizz check <dir> [--since ref] [--format f] [--profile p]` | quality gate; exit 1 when thresholds fail | CI |
| `analyze` | `docwizz analyze <dir> [--format f] [--profile p]` | documentation and architecture report, no gate | everyday |
| `architecture` | `docwizz architecture <dir> [--format f] [--profile p]` | layers, dependencies, violations, cycles; exit 1 above thresholds | architects |
| `diff` | `docwizz diff [dir] [base] [head] [--format f] [--profile p]` | change impact vs the last `generate` or git refs | reviews, CI |
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
| `--format console\|json` | `console` | analyze, check, architecture, diff, remediate | `json` prints one JSON document to stdout | CI, tooling |
| `--since <ref>` | — | check, remediate | check: compares against the tree at `<ref>`, so only introduced problems fail; remediate: says whether each update touches only what changed since `<ref>` | CI |
| `--package <name> --to <version>` | `remediation.targets` and version drift | remediate | given together or not at all | maintenance |
| `--validate` | off | remediate | runs the configured restore/build/test commands in a temporary copy, never the working tree | maintenance |
| `--html` | off | generate, setup | adds HTML pages; the Markdown is written either way | normal |
| `--progress` | interactive terminals | generate, setup | Force progress bars on stderr when output is redirected | normal |
| `--refresh-html-on-version-change` | off | generate, setup | Implies HTML; rebuild on generator version change | normal |
| `--ai` | off | generate, setup | local Ollama only; cached drafts are used even without it, nothing new is sent | opt-in |
| `--force` | off | setup | replaces an existing `docwizz.yaml`; any other command rejects it | setup only |
| `--timings` | off | all | time and peak memory per stage, on stderr | benchmarks |
| `--help`, `-h` | — | all | prints the command's help and exits | — |
| `--version`, `-v` | — | all | prints `docwizz <version> (commit <sha>, .NET <runtime>, <platform>)` and exits 0, whatever the command; `docwizz version` does the same | bug reports, CI logs |

## Precedence and conflicts

- **Configuration file:** `<dir>/docwizz.yaml`, else `./docwizz.yaml` in the current directory, else the built-in
  defaults. `setup` never reads the current directory's file. It uses `<dir>/docwizz.yaml` or writes one there.
- **Profile:** `--profile` beats `profile:` in the file, which beats `default`. A file with its own `patterns:` uses
  them unless `--profile` is given.
- **Existing configuration:** `init` never overwrites. `setup` keeps the file unless you pass `--force`.
- **Options a command doesn't use** (for example `--format` with `generate`, or `--html` with `analyze`) are
  rejected, with the commands that do take them. An option is never silently ignored. Only `--help`, `--timings` and
  `--version` apply everywhere.
- **`--version` first:** with `--version` (or `-v`), docwizz prints the version and exits 0 instead of running the
  command, even when `--help` is given too. Bad arguments are still reported first.
- **Mistakes** (an unknown command or option, a missing value, `--format xml`, `--package` without `--to`) print one
  line saying what is wrong, with a suggestion for a likely typo, and where to find the command's options. Exit 1.
- **`diff` arguments:** if the first argument is not a directory, it is a ref and the directory is `.`. One ref
  compares against the working tree, two refs compare against each other, and none compares against the last
  `generate`.

## Exit codes

| Command | 0 | 1 |
|---|---|---|
| `setup` | every stage ran (`SUCCESS` or `SUCCESS_WITH_FINDINGS`) | a stage failed (`FAILED`; the others are still reported), or bad arguments |
| `check` | thresholds pass | thresholds fail, or bad arguments/config |
| `architecture` | within `max_violations`/`max_cycles` | above them |
| `analyze`, `generate`, `scan`, `sbom` | done | bad arguments or config |
| `remediate` | done (suggestions alone never fail) | a `--validate` run failed, or bad arguments |
| `diff` | done | no baseline (`generate` first or pass a ref), unknown ref |
| `init` | written | `docwizz.yaml` exists |
| no command | — | prints how to start |
| `--version`, `version` | prints the version | — |

Every command exits **2** when docwizz itself fails unexpectedly (a bug, a full disk, a permission error). It prints one
line instead of a stack trace, so CI can tell "docwizz broke" from "the gate failed". Set `DOCWIZZ_DEBUG=1` for the
full trace.

## Versioning

```
$ docwizz --version
docwizz 0.1.0 (commit e4320bd, .NET 10.0.12, linux-x64)
```

The line names the docwizz version, the git commit it was built from, the .NET runtime and the platform: paste it into
bug reports, and print it at the start of CI jobs so a result can be traced to the build that produced it. A build
made outside a git checkout has no commit and leaves it out. `docwizz setup` writes the same version into the header
of the `docwizz.yaml` it creates.

Versions follow [Semantic Versioning](https://semver.org): `MAJOR.MINOR.PATCH`, with a pre-release suffix such as
`-rc.1` when needed. While the major version is `0`, a minor release may change command-line options or output. The
git tags (`vX.Y.Z`) are the version: the release workflow builds with `-p:Version` from the tag it creates, and any
other build derives it from the tags in its checkout ([DocWizz.csproj](src/DocWizz/DocWizz.csproj)):

| Built from | `docwizz --version` |
|---|---|
| a release tag (`v0.2.2`) | `0.2.2` |
| 4 commits after `v0.2.2` | `0.2.3-dev.4`: a pre-release of the next patch |
| no git, or no tags (a shallow clone, a source archive) | `0.0.0-dev` |

`git fetch --tags` updates the tags of an existing clone. The commit is added by the .NET SDK at build time.

### Releases

Every pull request merged into `main` is a release. The [release workflow](.github/workflows/release.yml) takes the
latest `vX.Y.Z` tag, bumps it according to the PR's label, builds and tests that version, then tags the merge commit
and creates a GitHub release with generated notes. Nothing is committed to `main`:

| PR label | Release |
|---|---|
| none | patch: `0.1.0` → `0.1.1` |
| `release:minor` | minor: `0.1.3` → `0.2.0` (new features) |
| `release:major` | major: `0.4.2` → `1.0.0` (breaking changes) |
| `release:none` | nothing (docs, CI); the next merge without it releases |

- **No tag yet:** the first release is `0.1.0`.
- **Two release labels on one PR** fail the workflow instead of guessing. Remove one and re-run the job.
- **A commit already covered** by a newer tag (a queued run that finished late) is not released again.
- The bump rule is [semver-next.sh](.github/scripts/semver-next.sh). [semver-next.test.sh](.github/scripts/semver-next.test.sh)
  specifies it, and the release workflow runs it before every release.

## Generated vs. your files

| Path | Who owns it |
|---|---|
| `docwizz.yaml` | **you**. `setup`/`init` create it once and then leave it alone (`setup --force` replaces it). |
| `docs/*.md`, `docs/modules/`, `docs/views/`, `*.html` | docwizz. Regenerated, so don't edit them. |
| `docs/architecture/*.md` | **you**. Linked from the generated pages and never overwritten. |
| `docs/.docwizz/model.json`, `documentation.json` | docwizz: the baseline `diff` compares against, and the documentation model as data. |
| `docs/.docwizz/ai-cache.json`, `ai-assessments.json` | docwizz: AI drafts and ratings, cached by code hash. Commit them to avoid re-sending code. |
