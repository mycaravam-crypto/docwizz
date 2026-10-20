# Requirements and traceability

[Documentation home](../README.md) · [CLI reference](../cli.md)

DocWizz combines **three independent evidence sources**, without inventing missing relationships:

1. **ADO work-item catalog:** the complete area inventory, including requirements with no history or code links. [See the area-work-item workflow](#area-path-work-item-inventory).
2. **GitWizz historical evidence:** provider-verified commit links (`confirmed`) and weaker PR/message associations (`candidate`). [Contract and interpretation](../reference/requirement-formats.md).
3. **Explicit test and architecture evidence:** ADO requirement-to-test-case relationships and declared requirement → architecture → test relations. [Project-evidence contract](project-evidence.md).

```bash
ado workitem export --project Shop --area "Shop\\Orders" --output workitems.json
docwizz requirements catalog-import . workitems.json
# Optional: docwizz requirements import . history.json work-items/
# Optional: docwizz tests import . test-trace.json
docwizz generate . --html
```

The generated area catalog gives each work item a page. No history index is required. Its source-scoped history and test links are evidence of **relationships**, not verified implementation, execution or coverage. `not-assessed` (no source), `not-linked` (checked source with no link), and `unavailable` (scope/incomplete source) are distinct.

For detailed schemas and limits read [area requirements](#area-path-work-item-inventory), [requirement history](../reference/requirement-formats.md), and [explicit project evidence](project-evidence.md). Sensitive work-item titles, acceptance criteria and generated HTML/search files require controlled access.

## Offline Azure DevOps test snapshots

`docwizz tests import . alpha-tests.json` accepts both single-plan `ado.test-traceability/v1` and multi-plan `ado.test-traceability/v2` snapshots; v2 preserves explicit plan/suite/testcase assignments. It validates these references and stores the snapshot offline.

`docwizz tests import . alpha-tests.json` validates `ado.test-traceability/v1` exported by `ado testplan trace-export` and saves a normalized snapshot under `docs/.docwizz/ado-tests-<planId>.json`. The command reports explicit requirement-to-test-case associations. It does not infer relationships to code tests or assert test execution. The generator adds a separate `tests.md` page (and an HTML page with `--html`) showing explicit plan, requirement and test-case associations; it never asserts execution or coverage.

## Area-path work-item inventory


DocWizz imports a complete, read-only work-item inventory from **ADO** and enriches it with two independent sources: historical GitWizz/ADO code links and offline ADO test-case snapshots. The catalog works when neither optional evidence source is present. All import and generation steps are local; DocWizz makes no Azure DevOps requests.

### Example workflow

```bash
# In the application repository, with ado-devops configured for the desired collection.
ado workitem export --project Shop --area "Shop\Orders" --output shop-workitems.json

# Optional: historical code links. Export enough Git history to cover the relevant period.
gitwizz history export --since v1.0.0 --provider azure-devops -o history.json
mkdir -p work-items
ado workitem context 42 > work-items/42.json
docwizz requirements import . history.json work-items/

# Optional: explicit test-case relationships. Use an ADO test-traceability v1/v2 JSON export.
ado testplan trace-export --project Shop --area "Shop\Orders" --output shop-tests.json
docwizz tests import . shop-tests.json

# This step always works, even without the optional imports above.
docwizz requirements catalog-import . shop-workitems.json
docwizz generate . --html
```

**Paths and CLI options:** consult the installed versions of the ADO exporter and test-plan exporter for options. The catalog producer writes `ado.workitem-catalog/v1` with `schema`, `project`, `area`, `source`, `complete` and `items`. Incomplete catalogs are rejected, rather than being presented as complete requirement coverage.

### Documentation output

- `docs/requirements/area-catalog.md` and its HTML counterpart: all work items in the exported area, including planned items with no code history; parent links are explicit ADO links.
- `docs/requirements/items/<id>.md` and HTML: description, acceptance criteria, children, provider-linked code symbols, code-test references and ADO test cases.
- The project index, search index and HTML navigation link to the area catalog independently of `docs/requirements/index.md` (historical links).

The exported ADO `AreaPath` selects the area and its descendants; it is **not** treated as evidence of a parent-child relationship.

### Semantics

| Status | Meaning |
|---|---|
| `confirmed link` | An ADO-provider-confirmed commit associated with this work item touched the current symbol. Not proof of implementation. |
| `candidate link` | A weaker historical association (PR or message). Not proof of implementation. |
| `not-linked (imported history)` | The imported, available history contains no code link for this work item. Not proof that it is unimplemented. |
| `not-assessed` | No relevant evidence source was imported. |
| `unavailable` | An import is incomplete, belongs to a different ADO origin/project, or a requirement is missing from a test snapshot. No positive or negative coverage claim is made. |
| `linked (ADO cases)` | Explicit ADO requirement-to-test-case relationship, **not** execution, pass/fail, test coverage or acceptance verification. |
| `referenced by test code` | A structural test-code reference to a linked symbol, **not** coverage or a demonstrated pass. |

The history overlay uses stable ADO work-item IDs **only after** verifying that the GitWizz history refers to the same ADO server/collection and project as the catalog. GitHub issue IDs are never silently treated as ADO IDs. Importing a full area catalog does **not** change or overwrite `docs/.docwizz/requirements.json` or its confirmed/candidate semantics.

### Security and repeatability

The snapshot and pages can contain internal requirements, unpublished features, customer details and acceptance criteria. **Do not commit or publish those artifacts to a public repository or website unless reviewed and authorized.** DocWizz ignores the new default catalog output paths in Git, but **other generated files (including search.json, search.js, index.html and other HTML pages) may still contain work-item titles**. Protect the entire generated documentation site, its web hosting and any custom output locations; do not rely on .gitignore as an access-control mechanism. Local input exports remain your responsibility.

Generation is deterministic for identical imported evidence and code models. The normal generator owns its Markdown and HTML pages and removes stale generated pages without deleting manual pages; import and generation do not call an LLM or external provider.

