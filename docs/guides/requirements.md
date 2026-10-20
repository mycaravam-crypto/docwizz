# Requirements and traceability

[Documentation home](../README.md) · [CLI reference](../cli.md)

DocWizz combines **three independent evidence sources**, without inventing missing relationships:

1. **ADO work-item catalog:** the complete area inventory, including requirements with no history or code links. [Import it](../area-requirements.md).
2. **GitWizz historical evidence:** provider-verified commit links (`confirmed`) and weaker PR/message associations (`candidate`). [Contract and interpretation](../requirement-context.md).
3. **Explicit test and architecture evidence:** ADO requirement-to-test-case relationships and declared requirement → architecture → test relations. [Project-evidence contract](../project-evidence.md).

```bash
ado workitem export --project Shop --area "Shop\\Orders" --output workitems.json
docwizz requirements catalog-import . workitems.json
# Optional: docwizz requirements import . history.json work-items/
# Optional: docwizz tests import . test-trace.json
docwizz generate . --html
```

The generated area catalog gives each work item a page. No history index is required. Its source-scoped history and test links are evidence of **relationships**, not verified implementation, execution or coverage. `not-assessed` (no source), `not-linked` (checked source with no link), and `unavailable` (scope/incomplete source) are distinct.

For detailed schemas and limits read [area requirements](../area-requirements.md), [requirement history](../requirement-context.md), and [explicit project evidence](../project-evidence.md). Sensitive work-item titles, acceptance criteria and generated HTML/search files require controlled access.
