# Decision: a separate product template contract

Status: accepted, implemented in `docwizz product`.

## Context

Documentation profiles ([Profiles.cs](../../../src/DocWizz/Model/Profiles.cs)) say which code symbols need which
documentation sections. A work product such as a V-Modell XT software architecture is a different thing: most of its
content (project intent, requirements, design decisions, approval) can't be derived from code.

## Decision

Work products get their own small YAML contract, independent of profiles, generation and the AI drafts:

- **Template, schema 1:** a product id, V-Modell XT variant and version or a generic `document_type`, project-specific
  tailoring, ordered sections (id, title, `required`, allowed evidence categories `code` and `project`) and direct
  product dependencies. Unknown fields, missing values, duplicate ids and ambiguous booleans are rejected before
  anything is generated; `schema` must be the plain integer `1` and `required` a plain `true` or `false`.
- **Code evidence by convention:** the section ids `structure` (top-level building blocks) and `interfaces` (code
  dependencies) are filled from the code model, at most 40 entries each. Other sections are never filled from code;
  renaming a reserved id turns its mapping off. Explicit selectors can replace these conventions in a later schema
  once more than two mappings are needed. Evidence is a plain `file:line`, not a relative Markdown link.
- **Project evidence:** `statements: [{section, text, source}]` with a repository-relative Markdown source under
  `docs/`. Statements are maintained by people, not approved by them; the parser checks format, not truth. Multi-line
  and approval-like statements are rejected, and a statement must name a section that accepts project evidence.
- **Open sections:** a section without evidence says `OFFEN`. Nothing is made up to fill it.
- **AI wording:** `--ai` sends each section's evidence, with document-wide ids (`E1`, …), through the existing
  provider interface and endpoint guard. A sentence is kept only when it cites an id of its own section and claims no
  status, approval, completeness or conformity. Without `--ai`, or when nothing survives, the output is byte-identical
  to the deterministic draft. The cache key is template id, section id, a hash of instructions and evidence, and
  provider and model.
- **Evidence report:** `<product_id>.evidence.json` next to the draft lists per section its status, the evidence with
  ids and origin, the synthesized sentences with citations, and the required-section totals. `product check`
  verifies references and freshness from it, never content.

## Consequences

- Profiles, generation and the AI drafts are unchanged by products.
- V-Modell XT ids and versions are metadata, not a conformity or licensing claim. The bundled templates are original
  illustrative German outlines; rights to reuse official template wording must be checked before adding any.
- V-Modell states (*in Bearbeitung*, *vorgelegt*, *fertiggestellt*) are not modelled: they need a documented human
  review. Approval happens outside docwizz, or through signed [publication](../../reference/publication.md).
