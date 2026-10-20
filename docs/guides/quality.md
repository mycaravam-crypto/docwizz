# Quality checks and change review

[Documentation home](../README.md) · [CLI reference](../cli.md)

## Architecture analysis

```bash
docwizz architecture .
docwizz architecture . --format json
```

Lists the layers, the dependencies between them, violations (each `ARCH-…` rule with a severity) and module cycles.
Set the layers (path globs, first match wins) and `architecture.allow` in `docwizz.yaml`. `check.fail_on` sets the
lowest severity that counts against `max_violations`; lower ones are only reported. Custom rules:
[README: Architecture rules](../../README.md#architecture-rules).

## Quality gates and CI

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

## Diff and impact analysis

```bash
docwizz diff .                # vs the docs last generated (docs/.docwizz/model.json)
docwizz diff . origin/main    # vs a git ref
docwizz diff HEAD~1 HEAD      # between two refs (directory defaults to .)
```

Lists changed, added and removed symbols, the pages they affect, docs that may now be stale, new gaps and violations,
and the test code linked to each changed symbol. A test link means test code *uses* the symbol; it is not coverage.

## Raw model (advanced)

```bash
docwizz scan . model.json
```

Writes every node and edge the scanners found: for debugging why a symbol is or isn't reported, or building your own
tooling. Everyday workflows don't need it.
