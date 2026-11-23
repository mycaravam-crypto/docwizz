# Project evidence and traceability

[Documentation home](../README.md) · [CLI reference](../cli.md) · [Product documents](product-documents.md)

A project evidence catalog is a file you maintain that lists requirements, architecture elements and tests, and the
links you declare between them. `docwizz trace` reports the gaps in those links and `docwizz evidence-diff` compares two
versions of the catalog. Nothing is inferred: only declared records and links count. Neither command makes network or
AI calls, writes files or claims compliance.

This is separate from [requirement history](requirements.md), where links come from Git and are labeled by how they
were found; nothing from the history index is ever written into a catalog.

## The catalog

```yaml
schema: 1
evidence:
  - id: REQ-017
    kind: requirement
    text: A user must authenticate.
    source: docs/requirements.md
    version: v1
    source_hash: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef   # optional
  - id: ARC-004
    kind: architecture
    text: AuthService handles authentication.
    source: docs/architecture.md
    version: v2
  - id: TEST-032
    kind: test
    text: A successful sign-in is tested.
    source: docs/tests.md
    version: v1
links:                    # optional
  - { from: REQ-017, to: ARC-004 }
  - { from: ARC-004, to: TEST-032 }
```

- **Records:** a unique uppercase id (`PREFIX-123`), a `kind` (`requirement`, `architecture`, `test`), single-line
  `text`, a repository-relative Markdown `source` under `docs/`, and a non-empty `version`.
- **Links:** only `requirement → architecture` and `architecture → test`, between existing ids. Duplicates, unknown ids
  and other directions are rejected; cycles are impossible by construction.
- **`source_hash`:** the SHA-256 of the source file's bytes (lowercase hex), to detect when the source changed.
- Unknown keys, duplicate ids, malformed documents and invalid or symlinked paths are rejected with the record's
  index.

**CSV and Markdown** catalogs hold records only, without links or `source_hash`. CSV needs the exact header
`id,kind,text,source,version` with comma separators (choose comma-separated, not semicolon-separated, when exporting
from Excel). Markdown needs one five-column pipe table with the same header:

```markdown
| id | kind | text | source | version |
|---|---|---|---|---|
| REQ-017 | requirement | Authenticate users | docs/requirements.md | v1 |
```

## Traceability report

```bash
docwizz trace . project-evidence.yaml [--format json]
```

A Markdown matrix or JSON on stdout. Each requirement is `linked`, `partial` (one of several architecture records has
no test), `no-architecture` or `no-test`; architecture records without incoming or outgoing links and tests without an
incoming link are listed separately. `OPEN` marks missing declared evidence, not a failed test.

Each source with a `source_hash` is reported as `current` (the file matches), `stale`, or `missing`; one without is
`unverified`. Findings never fail the command; an invalid catalog exits 1. Paths resolve from the current directory,
then from `<dir>`.

## Comparing two catalogs

```bash
docwizz evidence-diff . before.yaml after.yaml [--format json]
```

Lists added, removed and changed records (with the fields that changed: `kind`, `text`, `source`, `version`), their
directly linked ids, added and removed links, and `affectedTests`: the tests reachable from a changed record along
declared links in either version. It compares the catalogs only, not the source files they point to: use
`source_hash` and `trace` for that.
