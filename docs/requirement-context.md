# Requirement context

DocWizz can show which historical requirements (Azure DevOps work items) a code symbol was changed for, with the exact
commit, pull request, file and lines behind each link. It reads files that other tools exported; it never contacts
Azure DevOps or a Git server itself.

```bash
gitwizz history export --since <ref> --provider azure-devops -o history.json   # gitwizz.history/v2
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
| `confirmed` | a commit the **provider** links to the work item (a verified commit link, `providerLinks`) changed lines inside the symbol |
| `candidate` | only a **confirmed pull request** is linked to the work item (also when the commit's PR attribution is ambiguous), or a commit that changed the symbol **mentions** it in its message (`AB#123`), and nothing verified links it |

A pull request that touched a symbol is no proof that the symbol implements every work item of that PR, and a message
naming a work item is nobody's verified link, so both stay `candidate` unless a provider commit link confirms them. Each link also says `changedSince`: whether the symbol's
code changed after the newest commit behind the link. A requirement describes the code as it was then.

Changed line ranges are mapped onto symbols as they were **at that commit**: each exported commit that changed scanned source files
is scanned as of that commit (`git archive`, the same scan as `docwizz diff`). A range links only the innermost symbols
whose lines it overlaps; a pure deletion links the symbol around the gap. A range that overlaps no member (a `using`, a
field between methods) links only the type around it, or only the file. A file-granularity change (binary, a rename or
mode change without content) is never mapped to a symbol. `show` lists such commits under `fileLevel` without
asserting anything about the symbol. Merge commits carry no changes of their own: their parents' commits do.

Every answer has an `outcome`:

| Outcome | Meaning |
|---|---|
| `linked` | at least one requirement (`complete: false` when some linkage couldn't be read) |
| `no-link` | every commit that changed the symbol has a verified, empty work-item linkage |
| `unavailable` | no requirement found, and some linkage was `unavailable`, `denied` or `unknown`, a PR it came with is unconfirmed, or an exported commit is missing locally |
| `not-in-history` | no exported commit changed the symbol |
| `not-current` | the index knows the id from history but the current code doesn't (renamed, moved or deleted); `--for` takes the old id |

When the history ends before `HEAD`, `index.coversHead` is `false` and a warning says later commits are not covered;
a truncated export or one from a shallow clone says the same about older commits. When nothing in the export was
verified (a `local` or `github` history, or every lookup failed), `import` warns that a symbol without requirements is
unknown, not unlinked.

## Inputs

### `gitwizz.history/v2`

What `gitwizz history export` writes, read as is: no conversion step, no adapter. gitwizz owns the contract
([docs/history/README.md](https://github.com/mycaravam-crypto/gitwizz/blob/main/docs/history/README.md), with a JSON
Schema and an example). DocWizz reads only this version; any other `schema` fails with the reason, and every
enumeration is closed, so a newer contract fails instead of being misread. A `gitwizz.history/v1` file (the shape
DocWizz read before, never a gitwizz contract) fails with a note to export again.

```bash
gitwizz history export --since v1.4.0 --provider azure-devops -o history.json
docwizz requirements import . history.json work-items/
```

How DocWizz reads it:

| Export | Index |
|---|---|
| `commits[].providerLinks` `verified` with `AB#n` items | direct commit links: `confirmed`. `[]` is a verified "none" |
| `providerLinks` `unavailable`, `denied`, `unknown` | no answer: the symbol's outcome is `unavailable`, never `no-link` |
| `commits[].workItems` (`source: commit-message`) | mentions: `candidate` at most, never confirmed |
| `commits[].pullRequest` `exact` / `inferred` / `ambiguous` (`candidates`) / `unknown` | the commit's PR, with that confidence; `unknown` has none |
| `pullRequests[]` `status: confirmed` with `workItemsStatus: verified` | the PR's work items: `candidate` for the PR's commits |
| `pullRequests[]` `unconfirmed` or `local` | not used, with a warning; symbols it covers are `unavailable` |
| `files[]` `granularity: lines` with `ranges` | mapped onto symbols as of the commit |
| `files[]` `granularity: file` | file-level evidence only |
| `kind: merge` | no changes of its own |
| `range.untilSha`, `truncated`, `shallow`, `provider`, `repository` | coverage and identity in the index's `inputs` |

Work items are Azure Boards ids (`AB#123`). A `#123` names a GitHub issue (from a GitHub or local history): it is
listed in a warning and not indexed, as DocWizz holds only Azure DevOps work items.

`tests/DocWizz.Tests/Golden/gitwizz/history-v2-example.json` is gitwizz's own example export, unchanged;
`GitwizzExampleJourneyTests` rebuilds the repository it was exported from and imports it end to end.

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

`docs/.docwizz/requirements.json` (`docwizz.requirement-index/v2`) holds facts only: per commit its provider
linkage and links, message mentions, PR attribution and touches (symbol id at that commit, its body hash then, changed
lines, symbol lines); per PR its status and linkage; per work item its cleaned fields and the SHA-256 of its export. Its `fingerprint` covers the history file,
every work item's digest, the scan scope and the index schema.

- Identical inputs: `import` prints `unchanged` and writes nothing. Rebuilding gives the same bytes.
- New history: commits already mapped under the same scope are reused; only new ones are scanned.
- A new work-item revision changes the fingerprint; only that item's content changes in the index.
- Commit the index or not, as you like: it contains work-item titles and criteria, so treat it like the exports.

An index from an older DocWizz (`docwizz.requirement-index/v1`) is rebuilt by the next `import`; `show` asks for that.

Exit codes: `import` exits 1 for a missing file, invalid JSON, an unknown schema, a missing field or unknown
enumeration value (with its path), a short SHA or a duplicate commit;
`show` exits 1 without an index or for an unknown target. Neither writes outside `docs/.docwizz/`.

## Using it in drafts and agent context

`docwizz generate . --ai --requirements`, `docwizz context . --for <symbol> --requirements` and the MCP tools
`requirements_for_symbol`, `symbols_for_requirement` and `context(requirements: true)` read this index; none of them
fetches anything. The rules (bounded selection, untrusted data, `intent`/`deviation` sections, labels, cache keys) are
in [CLI.md](../CLI.md#requirements-in-drafts). Without `--requirements` nothing changes.
