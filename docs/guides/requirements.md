# Requirements and traceability

[Documentation home](../README.md) · [CLI reference](../cli.md)

docwizz combines three independent kinds of requirement evidence. Each is optional, each is labeled with where it
came from, and none is used to invent a link the others lack:

| Source | Import | What it adds |
|---|---|---|
| Azure DevOps work-item catalog | `requirements catalog-import` | every work item of an area path, including ones with no code or history yet |
| Git history ([GitWizz](https://github.com/mycaravam-crypto/gitwizz) export) | `requirements import` | which work items or GitHub issues a symbol was changed for: `confirmed` or `candidate` |
| Azure DevOps test plans | `tests import` | the test cases explicitly linked to each requirement |

Hand-declared requirement → architecture → test links are a separate, fourth mechanism: [project evidence](project-evidence.md).

Every import is local: docwizz reads files that `ado`, `gitwizz` and `gh` exported and never contacts Azure DevOps,
GitHub or a Git server itself, except in the [one-step export](#exporting-from-azure-devops-in-one-step) you opt into.

## Typical workflow

```bash
# Work-item catalog: the whole area, always works on its own
ado workitem export --project Shop --area "Shop\Orders" --output shop-workitems.json
docwizz requirements catalog-import . shop-workitems.json

# Optional: history links. Export enough history to cover the period you care about.
gitwizz history export --since v1.0.0 --provider azure-devops -o history.json
mkdir -p work-items && ado workitem context 42 > work-items/42.json    # one file per work item
docwizz requirements import . history.json work-items/

# Optional: explicit test-case links (ado.test-traceability v1 or v2)
ado testplan trace-export --project Shop --area "Shop\Orders" --output shop-tests.json
docwizz tests import . shop-tests.json

docwizz generate . --html
```

The next `generate` adds:

- `requirements/area-catalog.md`: every work item of the exported area, with its parent links from ADO;
- `requirements/items/<id>.md` and, with history, `requirements/<id>.md`: description, acceptance criteria, children,
  linked routes and current code symbols with commits and pull requests, code-test references and ADO test cases;
- a Requirements section on each route page and work-item backlinks on module pages;
- `tests.md`: test plans and the explicit requirement → test-case links;
- *Traceability gaps* in `quality.md`: routes without work items or endpoints, work items without current code.

The console report of `generate` says which of these sources are imported, missing or invalid. Missing sources are
reported and never fail the generation.

## What a status means

| Status | Meaning |
|---|---|
| `confirmed link` | a commit the provider links to the work item changed the current symbol |
| `candidate link` | a weaker association: only through a pull request, a commit-message mention or a rename |
| `not-linked (imported history)` | the imported history has no code link for the work item |
| `not-assessed` | no evidence source for this question was imported |
| `unavailable` | an import is incomplete, belongs to another ADO organization or project, or a snapshot lacks the requirement |
| `linked (ADO cases)` | an explicit requirement → test-case relationship in Azure DevOps |
| `referenced by test code` | test code calls or constructs a linked symbol |

None of these says a requirement is implemented, tested or accepted: a link is evidence of a relationship, and its
absence is not evidence of a gap. [Evidence levels](../reference/evidence-levels.md) explains the distinctions.
Acceptance criteria stay *unverified* because no test results are imported.

The history overlay on the catalog uses ADO work-item ids only after checking that the history refers to the same
ADO organization and project; GitHub issue ids are never treated as ADO ids. Importing a catalog doesn't change the
history index. A catalog that isn't marked `complete` is rejected rather than shown as the whole area.

History formats, link rules, GitHub issues and the index: [requirement formats](../reference/requirement-formats.md).

## Exporting from Azure DevOps in one step

`generate` can run the exports itself before it writes the docs:

```bash
docwizz generate . --html --ado-project Shop --ado-area 'Shop\Orders' --history-since v1.0.0
```

It exports and imports the area catalog and the test-case links, and with `--history-since` also the GitWizz history
after that ref and the context of every exported work item. It needs `ado` (and `gw` for history) on `PATH`, already
signed in. Before exporting anything it checks that the tools start and, with `--history-since`, that the ref resolves
to a commit; authentication and export failures can still happen later. Without `--ado-project` and `--ado-area`,
`generate` stays offline.

The programs run with an argument array, never a shell. Their output is staged in a private temporary directory,
schema-checked against the requested project and area, imported only when every export succeeded, and deleted
afterwards. If an import fails, the previous evidence is restored; this is a rollback, not an atomic switch for
concurrent readers. A terminal progress bar shows the export stages; `--progress` logs them when stderr is redirected.

**Windows / Git Bash:** if `gw` works in Git Bash but `generate` can't start it, `gw` is a shell alias, which a .NET
process can't launch. Point docwizz at the GitWizz assembly instead:

```bash
export DOCWIZZ_GITWIZZ_DLL="$(cygpath -w "$HOME/tools/gitwizz/gitwizz.dll")"
```

[scripts/sync-ado-evidence.sh](../../scripts/sync-ado-evidence.sh) does the same with the separate CLIs, for setups
that prefer a script (`bash sync-ado-evidence.sh Shop 'Shop\Orders' v1.0.0`, run from the application repository;
needs `jq`). Its imports run one after another without a rollback.

## Confidentiality

Catalogs, history indexes and the generated pages contain work-item titles, descriptions and acceptance criteria:
internal requirements, unpublished features, customer details. The default import locations are ignored by Git, but
the generated pages, `search.json`, `search.js` and every HTML page may contain these texts too. Protect the whole
generated site and its hosting; `.gitignore` is not access control.
