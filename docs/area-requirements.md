# Area-path requirements and traceability

DocWizz imports a complete, read-only work-item inventory from **ADO** and enriches it with two independent sources: historical GitWizz/ADO code links and offline ADO test-case snapshots. The catalog works when neither optional evidence source is present. All import and generation steps are local; DocWizz makes no Azure DevOps requests.

## Example workflow

\`\`\`bash
# In the application repository, with ado-devops configured for the desired collection.
ado workitem export --project Shop --area "Shop\\Orders" --output shop-workitems.json

# Optional: historical code links. Export enough Git history to cover the relevant period.
gitwizz history export --since v1.0.0 --provider azure-devops -o history.json
mkdir -p work-items
ado workitem context 42 > work-items/42.json
docwizz requirements import . history.json work-items/

# Optional: explicit test-case relationships. Use an ADO test-traceability v1/v2 JSON export.
ado testplan trace-export --project Shop --area "Shop\\Orders" --output shop-tests.json
docwizz tests import . shop-tests.json

# This step always works, even without the optional imports above.
docwizz requirements catalog-import . shop-workitems.json
docwizz generate . --html
\`\`\`

**Paths and CLI options:** consult the installed versions of the ADO exporter and test-plan exporter for options. The catalog producer writes \`ado.workitem-catalog/v1\` with \`schema\`, \`project\`, \`area\`, \`source\`, \`complete\` and \`items\`. Incomplete catalogs are rejected, rather than being presented as complete requirement coverage.

## Documentation output

- \`docs/requirements/area-catalog.md\` and its HTML counterpart: all work items in the exported area, including planned items with no code history; parent links are explicit ADO links.
- \`docs/requirements/items/<id>.md\` and HTML: description, acceptance criteria, children, provider-linked code symbols, code-test references and ADO test cases.
- The project index, search index and HTML navigation link to the area catalog independently of \`docs/requirements/index.md\` (historical links).

The exported ADO \`AreaPath\` selects the area and its descendants; it is **not** treated as evidence of a parent-child relationship.

## Semantics

| Status | Meaning |
|---|---|
| \`confirmed link\` | An ADO-provider-confirmed commit associated with this work item touched the current symbol. Not proof of implementation. |
| \`candidate link\` | A weaker historical association (PR or message). Not proof of implementation. |
| \`not-linked (imported history)\` | The imported, available history contains no code link for this work item. Not proof that it is unimplemented. |
| \`not-assessed\` | No relevant evidence source was imported. |
| \`unavailable\` | An import is incomplete, belongs to a different ADO origin/project, or a requirement is missing from a test snapshot. No positive or negative coverage claim is made. |
| \`linked (ADO cases)\` | Explicit ADO requirement-to-test-case relationship, **not** execution, pass/fail, test coverage or acceptance verification. |
| \`referenced by test code\` | A structural test-code reference to a linked symbol, **not** coverage or a demonstrated pass. |

The history overlay uses stable ADO work-item IDs **only after** verifying that the GitWizz history refers to the same ADO server/collection and project as the catalog. GitHub issue IDs are never silently treated as ADO IDs. Importing a full area catalog does **not** change or overwrite \`docs/.docwizz/requirements.json\` or its confirmed/candidate semantics.

## Security and repeatability

The snapshot and pages can contain internal requirements, unpublished features, customer details and acceptance criteria. **Do not commit or publish those artifacts to a public repository or website unless reviewed and authorized.** DocWizz ignores the new default catalog output paths in its Git configuration, but custom output locations need equivalent controls. The local input exports remain your responsibility.

Generation is deterministic for identical imported evidence and code models. The normal generator owns its Markdown and HTML pages and removes stale generated pages without deleting manual pages; import and generation do not call an LLM or external provider.
