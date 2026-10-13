# Requirement context

DocWizz can show which historical requirements (Azure DevOps work items) a code symbol was changed for, with the exact
commit, pull request, file and lines behind each link. It reads files that other tools exported; it never contacts
Azure DevOps or a Git server itself.

```bash
gitwizz history export --since <ref> --format json > history.json          # gitwizz#84
ado workitem context 101 > work-items/101.json                              # ado-devops#83, one per work item
docwizz requirements import . history.json work-items/                      # → docs/.docwizz/requirements.json
docwizz requirements show . --for Shop.OrderService.Place --format json     # one symbol's requirements
docwizz requirements show . --work-item 101                                 # one work item's symbols
```

This is separate from [project evidence](project-evidence.md): there, people declare `requirement → architecture →
test` links by hand. Here, links come from history and are labeled by how they were found. Nothing from this index is
ever written into an evidence catalog.

## What a link means

| Status | Meaning |
|---|---|
| `confirmed` | a commit that is **directly** linked to the work item changed lines inside the symbol |
| `candidate` | only a completed **pull request** is linked to the work item (or the commit's PR association is ambiguous), and a commit of that PR changed the symbol |

A pull request that touched a symbol is no proof that the symbol implements every work item of that PR, so a PR link
stays a `candidate` unless a direct commit link confirms it. Each link also says `changedSince`: whether the symbol's
code changed after the newest commit behind the link. A requirement describes the code as it was then.

Hunks are mapped onto symbols as they were **at that commit**: each exported commit that changed scanned source files
is scanned as of that commit (`git archive`, the same scan as `docwizz diff`). A hunk links only the innermost symbols
whose lines it overlaps; a pure deletion links the symbol it was deleted from. A hunk that overlaps no member (a
`using`, a field between methods) links only the type around it, or only the file, and `show` lists such commits under
`fileLevel` without asserting anything about the symbol.

Every answer has an `outcome`:

| Outcome | Meaning |
|---|---|
| `linked` | at least one requirement (`complete: false` when some linkage couldn't be read) |
| `no-link` | every commit that changed the symbol has a verified, empty work-item linkage |
| `unavailable` | no requirement found, and some linkage was `unavailable`, `denied` or `unknown`, or an exported commit is missing locally |
| `not-in-history` | no exported commit changed the symbol |
| `not-current` | the index knows the id from history but the current code doesn't (renamed, moved or deleted); `--for` takes the old id |

When the history ends before `HEAD`, `index.coversHead` is `false` and a warning says later commits are not covered.

## Inputs

### `gitwizz.history/v1`

Commits oldest first, with full SHAs, repository-relative paths and zero-context hunks. Until gitwizz#84 is released,
this is the shape DocWizz reads (`tests/DocWizz.Tests/Journey/FakeHistory.cs` writes it from a real repository):

```json
{
  "schema": "gitwizz.history/v1",
  "repository": { "provider": "azure-devops", "name": "shop", "url": "https://ado.example/_git/shop" },
  "range": { "base": "v1.0", "baseSha": "…", "head": "main", "headSha": "<40 hex>" },
  "commits": [
    {
      "sha": "<40 hex>",
      "changes": [
        { "path": "src/OrderService.cs", "oldPath": null, "status": "modified",
          "hunks": [ { "oldStart": 7, "oldLines": 0, "newStart": 7, "newLines": 1 } ] }
      ],
      "workItems": { "status": "verified", "items": [ { "id": 101, "source": "commit-workitem" } ] },
      "pullRequests": [ { "id": 5, "confidence": "exact" } ]
    }
  ],
  "pullRequests": [
    { "id": 5, "status": "completed", "title": "…", "mergeStrategy": "squash",
      "workItems": { "status": "verified", "items": [ { "id": 102, "source": "pr-workitem" } ] } }
  ],
  "warnings": [],
  "truncated": false
}
```

- `workItems.status`: `verified` (an empty list means none), `unavailable`, `denied` or `unknown`. Only `verified`
  answers "no link"; the others make the outcome `unavailable`.
- `pullRequests[].confidence`: `exact` or `ambiguous`. A PR that isn't `completed` is ignored with a warning.
- `status: "deleted"` files give file-level evidence only; a rename is mapped at its new path.

### `ado.requirement-context/v1`

One work item per file (or a JSON array of them), as `ado workitem context <id>` writes it (ado-devops#83). DocWizz
keeps `id`, `revision`, `currentRevision`, `isCurrent`, `type`, `state`, `title`, `webUrl`/`url`, `changedDate`,
`description.text` with its `sourceField`, and the acceptance-criteria `items` (or the non-empty lines of
`acceptanceCriteria.text`) with their `sourceField`. Raw HTML is never read. When several files carry one id, the
highest revision wins and a warning says so.

Work-item text is untrusted data: control characters are removed, whitespace is collapsed and fields are bounded
(title 300, description 2000, each criterion 500 characters, at most 50 criteria). Cuts are listed in the item's
`warnings`.

## The index

`docs/.docwizz/requirements.json` (`docwizz.requirement-index/v1`) holds facts only: per commit its linkage, PR
associations and touches (symbol id at that commit, its body hash then, hunk lines, symbol lines); per completed PR
its linkage; per work item its cleaned fields and the SHA-256 of its export. Its `fingerprint` covers the history file,
every work item's digest, the scan scope and the index schema.

- Identical inputs: `import` prints `unchanged` and writes nothing. Rebuilding gives the same bytes.
- New history: commits already mapped under the same scope are reused; only new ones are scanned.
- A new work-item revision changes the fingerprint; only that item's content changes in the index.
- Commit the index or not, as you like: it contains work-item titles and criteria, so treat it like the exports.

Exit codes: `import` exits 1 for a missing file, invalid JSON, an unknown schema, a short SHA or a duplicate commit;
`show` exits 1 without an index or for an unknown target. Neither writes outside `docs/.docwizz/`.
