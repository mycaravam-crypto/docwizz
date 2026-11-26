# Quality checks and change review

[Documentation home](../README.md) · [CLI reference](../cli.md) · [Configuration](../configuration.md)

## Documentation gaps

```bash
docwizz analyze .
docwizz analyze . --format json
```

Lists coverage per role and the critical gaps: public, complex or side-effecting code without the sections its
[profile](../configuration.md#profiles) requires, each with where it is, what is missing and why it needs docs. Written
docs are also checked for [quality](../configuration.md#doc-quality). `analyze` never fails; `check` is the gate.

## Architecture analysis

```bash
docwizz architecture .
docwizz architecture . --format json
```

Lists the layers, the dependencies between them, violations (each `ARCH-…` rule with a severity) and module cycles.
`check.fail_on` sets the lowest severity that counts against `max_violations`; lower ones are only reported. Layers,
allowed dependencies, custom and security rules: [architecture rules](../configuration.md#architecture-rules).

## Quality gates and CI

```bash
docwizz check .                      # everything: thresholds in `check:`
docwizz check . --since origin/main  # only what the change introduces
```

Thresholds live under [`check:`](../configuration.md). `--since` lets an existing codebase adopt the gate without
fixing everything first. It fails on new critical gaps, new failing violations, docs that now contradict the code, new
symbols over `max_complexity`, and, with `require_tests`, new symbols that no test code links to. *Possibly stale* docs
(signature changed, comment didn't) are listed but don't fail.

The GitHub Action runs `check --since` against the pull request's base and posts one comment, updated on every push,
including ADR candidates such as a new external system or layer dependency:

```yaml
on: pull_request
permissions: { contents: read, pull-requests: write }
jobs:
  docwizz:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
        with: { fetch-depth: 0 }                # the base commit must be in the checkout
      - uses: mycaravam-crypto/docwizz@vX.Y.Z   # a release tag or its commit SHA
        with:
          path: .                               # directory to scan
          # base: origin/main                   # default: the pull request's base commit
          # comment: 'false'                    # only the job summary
          # fail: 'false'                       # report, never fail
```

Pin the Action to a release tag or a release commit: it downloads the scanner build published with that release and
refuses a branch or other ref rather than scan with incomplete Java and frontend support
([scanner distribution](../reference/scanner-distribution.md)).

Any other CI system runs the command directly; the step fails when the gate fails (exit 1):

```bash
docwizz check . --since "$BASE_SHA" --format json > docwizz.json
```

To bootstrap a repository from a script, run `docwizz setup . --yes` and commit the `docwizz.yaml` it writes.

### Quality over quantity

`check` rejects oversized authored Markdown (DOC-001, 2,500 words) and dense paragraphs (DOC-002,
120 words). Fenced code, pipe-prefixed table rows and generated pages do not count. Wrapped lines
within a paragraph count together. Findings identify the file, line, measured size and a short
rewrite instruction. `--since` rejects increased excess per file and rule, not unchanged debt.

Tune the defaults in `docwizz.yaml`:

```yaml
check:
  doc_concision: true
  max_document_words: 2500
  max_paragraph_words: 120
```

Rewrite existing sections before adding new ones; remove duplication, retain evidence and decisions,
and split only at meaningful topic boundaries. These are readability heuristics, not proof of
semantic quality. Existing `exclude` globs apply to authored Markdown as well.

## Change impact

```bash
docwizz diff .                # vs the docs last generated (docs/.docwizz/model.json)
docwizz diff . origin/main    # vs a git ref
docwizz diff HEAD~1 HEAD      # between two refs (directory defaults to .)
```

```console
$ docwizz diff . HEAD~2 HEAD
Documentation impact (vs HEAD~2, at HEAD)

Changed (11)
  ~ CSharpScanner.Doc(Microsoft.CodeAnalysis.ISymbol)
  ...
Test traceability (2 of 14 changed symbols linked) — a link means test code uses the symbol; it is not code coverage
  ~ Orders.Place(int)  ← OrdersTests.Places
  No linked test (12)
    + Orders.Refund(int)  [high]
```

Lists changed, added and removed symbols, the pages they affect, docs that may now be stale, new gaps and violations,
updated package versions, and the test code linked to each changed symbol: directly, or through an interface or
implementation, by calling it or constructing it. Only test code inside the scanned directory is seen, so scan the
repository root when the tests live next to `src/`.

## Raw model

```bash
docwizz scan . model.json
```

Writes every node and edge the scanners found, for debugging why a symbol is or isn't reported or for your own
tooling. Everyday workflows don't need it; the format is described in [the code model](../internals/architecture.md#the-code-model).
