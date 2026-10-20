# Evidence-driven documentation generation

## Intention

`docwizz generate` should produce the fullest **defensible** representation of a software system from the sources already available: code and architecture graphs, detected technical tests, imported requirement history, explicit project evidence, Azure DevOps test-case traceability and measured load reports.

Generation should not stop at listing which imports exist. It should incorporate validated source facts into navigable documentation, connect records through **stable identifiers** where evidence establishes the relationship, and expose missing or stale sources. The result is a documented system view, not a claim of formal verification.

## Evidence levels

- **Observed:** static source structure, signatures, dependencies and repository files, cited to code.
- **Explicitly linked:** a producer-declared requirement-to-test-case edge (ADO) or separately maintained evidence relationship. A linked test case is **not** an executed test.
- **Historically associated:** requirement-to-code context from imported git/PR history. A historical association is **not** proof that current code implements the requirement.
- **Structurally inferred:** a technical unit test calls/constructs a symbol; this does **not** imply code coverage or the satisfaction of acceptance criteria.
- **Measured:** imported result events or load numbers, qualified by capture time and scope.

Never manufacture a missing connection using name similarity. Preserve per-source provenance and scope (project, Area Path, plan and snapshot time). Missing or invalid optional evidence must be visible but must not stop source documentation generation.

## Initial implementation

When a supported ADO `ado.test-traceability/v1` or `v2` snapshot was imported with `docwizz tests import`, `generate` validates it and emits `tests.md` (also HTML when `--html` is enabled). The page summarizes scopes, plans, requirements and explicit linked test cases. The generated index links to it. The console report distinguishes absent, valid and invalid imported tests and requirements.

A separately imported `ado.workitem-catalog/v1` area inventory is now represented in the generated Markdown and HTML requirements pages (including work items without code history). Compatible historical ADO code links, explicit ADO test-case links and structural test-code references are shown as **separate evidence categories**. They never assert fulfillment, executed test outcomes or a success rate: the interchanges do not contain such proof. See [area-path requirements](../guides/requirements.md).

## Remaining limitations

- ADO catalog/code evidence is joined only when provider identity and project match; unresolved origins remain unavailable rather than becoming false links.
- The area catalog distinguishes not assessed, not linked and unavailable. A combined formal verification or requirement coverage rate is **not** computed.
- An execution status would require a separate outcome-bearing, timestamped and scope-aware interchange contract. Work-item state and ADO test-case links do not supply that.

The generator stays offline, deterministic for identical inputs, and does not query Azure DevOps or execute tests.

## Navigation and presentation

Imported ADO test traceability is part of **Quality**, not a top-level documentation category. The HTML top navigation highlights Quality for the test page; the sidebar groups Quality overview and Tests & requirements under the same heading. Test plans and explicit requirement-to-test links render as Markdown tables that remain available in both Markdown and HTML output. Case counts are distinct within each plan and snapshot; an unlinked requirement is not a failed test. Technical test links and historical requirement context retain their separate provenance.
