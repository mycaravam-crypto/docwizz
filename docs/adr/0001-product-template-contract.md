# ADR-0001: Separate product template contract

Status: proposed (P0, issue #93).

## Context
`Profiles.cs` specifies documentation coverage for code symbols, not the content
of a V-Modell XT product. Code-derived views cannot establish project intent,
requirements, design decisions or product approval.

## Decision
Use one small YAML schema v1 for a product identifier, XT variant and version,
project-specific tailoring, ordered sections (identifier, title, required flag,
allowed evidence categories) and direct product dependencies. Reject unknown
fields, missing required values, duplicate identifiers and ambiguous booleans
before generation. `schema` must be the unquoted integer `1`; `required`
must be an unquoted `true` or `false`. Metadata identifiers remain non-empty
scalars; their format is not otherwise prescribed in P0.

`code` and `project` declare allowed evidence *categories*, not sources
already ingested. The included `sw-architecture.yaml` is an **illustrative
German-language** SW-Architektur example, **not an official V-Modell XT template**.

## Consequences / deliberately deferred
- No changes to `--profile`, `Generator`, `AiProse`, or existing CLI commands.
- No new model adapter, RAG, database, DOCX renderer or workflow engine in P0.
- P1 must leave unsupported project assertions **OFFEN**, never fabricate facts.
- XT identifiers/versions are metadata, not a conformity or licensing claim.
- P1 will define template lookup and dependency ID resolution; examples are
  not yet packaged with the CLI tool.
- Rights to reuse official template wording must be checked before inclusion.

## P1 renderer conventions

Schema v1 reserves section IDs `structure` (top-level code building blocks) and
`interfaces` (known code dependency edges) for deterministic CodeModel population.
Other IDs are not automatically filled; their content remains visibly open. Changing
a reserved ID disables that mapping. A future schema version can replace these
conventions with explicit selectors once more than two mappings are justified.
Evidence is a plain `file:line` reference, not a fragile relative Markdown URL.
Results are deliberately limited to 40 entries per section; this is a partial
inventory, not proof of completeness.

## Project context evidence (P1)

Project statements are **human-maintained, not human-approved**. Schema 1 accepts
`statements: [{section, text, source}]`; `source` must be a repository-relative
Markdown path beneath `docs/` (for example `docs/charter.md`). The parser checks
the format only, **not existence, document version, approval or truth**. A later
CLI integration must decide how to verify source existence and pin evidence
versions. Multi-line statements and approval-like status text are rejected;
statement section IDs must match a template section accepting `project` evidence.
Unsupported or renamed sections fail rather than silently dropping statements.
