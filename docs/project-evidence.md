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
check whether referenced files exist. These checks and explicit relationships
follow in #119–#122.

No inferred links, automated approval or LLM calls are made.
