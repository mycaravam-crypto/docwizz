# P2 project evidence (schema 1)

This is a **data contract**, not a new CLI command and not a compliance or review claim.
It complements the existing P1 `ProjectContext` statements; no existing template or
`docwizz product` behavior changes in P2.1.

An explicitly maintained YAML file lists typed, versioned evidence records:

```yaml
schema: 1
evidence:
  - id: REQ-017
    kind: requirement
    text: A user must authenticate.
    source: docs/requirements.md
    version: v1
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
```

Required fields: unique uppercase ID `PREFIX-123`, kind (`requirement`,
`architecture`, `test`), single-line text, repository-relative Markdown
source under `docs/`, and non-empty source version. Unknown keys, duplicate
IDs, malformed documents and invalid paths are rejected with indexed diagnostics.
A SHA-256 hash of each record's canonical field values changes with the text,
source path or version. **It is not a file-content hash**, and P2.1 does not
check whether referenced files exist. Source-file checks and traceability reports follow in #120–#122.

No inferred links, automated approval or LLM calls are made.

## Explicit relationships (P2.2, #119)

Schema 1 optionally accepts `links` after `evidence`. Each link has only
`from` and `to`, both existing evidence IDs. The only permitted directions are
`requirement → architecture` and `architecture → test`:

```yaml
links:
  - from: REQ-017
    to: ARC-004
  - from: ARC-004
    to: TEST-032
```

Duplicate links, unknown endpoints, wrong kind/direction, and unknown keys fail
with indexed diagnostics. Only explicit links count. Because allowed kinds advance
strictly from requirement through architecture to test, directed cycles are
impossible by construction; no separate cycle solver is needed. Existing YAML
files without `links` remain valid and yield an empty link list. This step
introduces no CLI command or inferred traceability; the gap report follows in #120.

## Traceability report (P2.3, #120)

`docwizz trace <dir> <evidence.yaml> [--format json]` renders explicit links as a stable Markdown matrix or machine-readable JSON on stdout. Each requirement is classified as `linked`, `no-architecture`, or `no-test`. Separate lists identify architecture records without incoming requirement links or outgoing test links. Invalid link IDs are rejected by the schema parser before reporting. Missing source documents and stale versions are **not** verified in this report, so it makes no freshness or conformity claim. Report findings do not affect exit status; invalid YAML does.

The report uses camelCase JSON keys. `partial` marks a requirement linked to multiple architecture records when at least one of those has no test; `orphanedTests` lists tests with no incoming architecture link. `OFFEN` in Markdown means missing explicit evidence, not a failed test. Source file names resolve from the current working directory first, then from `<dir>`, as for product templates.

## Illustrative product templates (P2.4, #121)

Three example structures live in `templates/vmodell-xt/`: `sw-architecture.yaml`,
`sw-specification.yaml`, and `system-element-test-specification.yaml`.
They use schema 1, describe V-Modell XT 2.4 as metadata, and deliberately
require project-specific tailoring. **These are original illustrative outlines,
not official licensed product templates or a conformity assertion.** Before
using them for a governed project, review the applicable XT variant/version,
the process tailoring, contents, and rights to reuse official text.

The test specification demonstrates the existing direct product dependency
contract with `dependencies: [sw-specification]`. That relationship is an
illustration, not a statement that the chosen project's tailoring always
requires it. The existing SW-architecture example deliberately keeps no dependency
so its P0/P1 demonstration remains independently runnable; adapt dependencies
to the project's actual tailored product chain. For requirement-to-test links,
use the explicit evidence relationships and `docwizz trace` report, not an
assumed relationship inferred from a chapter title. The existing `docwizz product check` verifies that the dependent
draft exists, not its professional approval or correctness. Chapters backed
only by project sources remain **OFFEN** until evidence is explicitly provided.
