# Requirement context

DocWizz can show which historical requirements (Azure DevOps work items or GitHub issues) a code symbol was changed for, with the exact
commit, pull request, file and lines behind each link. It reads files that other tools exported; it never contacts
Azure DevOps, GitHub or a Git server itself.

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
| `candidate` | only a **confirmed pull request** is linked to the work item (also when the commit's PR attribution is ambiguous), or a commit that changed the symbol **mentions** it in its message (`AB#123`), or the link belongs to the symbol's name **before a rename** (`renamed-from`), and nothing verified links it |

A pull request that touched a symbol is no proof that the symbol implements every work item of that PR, and a message
naming a work item is nobody's verified link, so both stay `candidate` unless a provider commit link confirms them. Each link also says `changedSince`: whether the symbol's
code changed after the newest commit behind the link. A requirement describes the code as it was then.

A rename keeps its links as candidates: when the commit that first changed a symbol has it in the same file, type and
parameters, at the line where a method that is gone from the current code last sat, that method's links come over as
`renamed-from` evidence naming the old id (`symbol`). `changedSince` then compares with the renaming commit. Only one
rename is followed, inside one file; a move to another file or type stays `not-current` under the old id.

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
| `not-current` | the index knows the id from history but the current code doesn't (renamed, moved or deleted); `--for` takes the old id; a rename in place also shows the links under the new id |

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

Azure Boards references (`AB#123`) retain their existing meaning. In a `github` history, `#123` is a GitHub issue
reference scoped to the history's repository. Commit-message mentions remain candidates; verified links through
confirmed PRs also remain candidates. Issue details never upgrade either to `confirmed`.

GitHub provider links must include the issue's `uri` (`https://github.com/owner/repo/issues/123`). Use a GitWizz
version that preserves closing-issue URLs; old exports without those URLs are skipped with an actionable warning,
because a closing reference might belong to another repository. Plain `#123` from `local` histories remains ambiguous
and is not indexed. PR-number markers stripped by GitWizz are never recovered from commit subjects by DocWizz.

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

### GitHub issue exports

Export issue details separately with `gh`; import itself stays offline:

```bash
mkdir -p issues
gitwizz history export --since <ref> --provider github -o history.json
gh issue view 123 --repo owner/repo --json number,url,title,body,labels,state,updatedAt > issues/123.json
docwizz requirements import . history.json issues/
docwizz requirements show . --for Shop.OrderService.Place --format json
docwizz requirements show . --work-item github#123
docwizz domain discover . --work-item github#123 --format json
```

An input file can contain one object or an array. The URL must identify an issue in the history's GitHub repository;
pull-request URLs, mismatched numbers and other repositories are rejected. This first version supports one GitHub
repository per index. GitHub CLI must support `gh pr view --json closingIssuesReferences` for history export
(validated with CLI 2.102.0); the issue-detail command is separate from that requirement.

The issue's bounded body is stored as `description`, with source `body`; labels are sorted and bounded to 20 names.
Only bullet/numbered items under an explicit Markdown `Acceptance criteria` or `Acceptance` heading become criteria;
code fences and unrelated prose/checklists do not. Body text is also available to bounded AI/agent context as
untrusted historical evidence. `bodyCut` reports when the AI budget truncates that text. Labels do not imply domain
concepts or requirements.

`updatedAt` identifies the exported snapshot. It is **not** an ADO revision or proof that the issue is current:
GitHub revision/current-revision/currentness fields are absent. When several exports name the same issue, the newest
exported `updatedAt` wins, then the content digest breaks ties deterministically. Editing an export changes the index
fingerprint; selected prompt text changes its AI cache key.

Identities are distinct: Azure Boards 123 stays numeric `123` in ADO JSON; GitHub 123 is
`github:owner/repo#123` internally and in versioned JSON. Human output and selectors use `github#123` relative to the
imported repository; the full identity is accepted too. Existing numeric, `WI-123`, `#123` and `AB#123` selectors mean
Azure Boards, including in a mixed index. GitWizz query output keeps its existing protocol, with requirement
`github#123`, a `repository` field and repository-qualified evidence.

## The index

`docs/.docwizz/requirements.json` holds facts only. ADO-only imports retain `docwizz.requirement-index/v2` and
numeric IDs; GitHub imports use `docwizz.requirement-index/v3`. Both versions can be read, so existing ADO indexes
need no migration. Reimport an old GitHub history with issue exports to rebuild its index. The contents are: per commit its provider
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
in [CLI.md](../CLI.md#requirements-in-drafts). Without `--requirements` the drafts and context don't change.

## In the generated docs

Whenever the index exists, `docwizz generate` also writes `requirements/index.md` and one page per work item: its
type, state, revision, description and acceptance criteria (numbered AC1… in export order), the routes and current
symbols it is linked to, with confirmed/candidate, "code changed since", commits and PRs. Route pages get a
Requirements section, and module pages a `_Work items:_` backlink. A route with no linked work item is listed as a gap.
Exported text is rendered as plain text: no HTML, links or images. No index: the route pages say none was imported.

## Answering gitwizz

`docwizz requirements query <dir> <query.json>` is the resolver gitwizz calls when `.gitwizz.yml` sets
`context.requirements.command: docwizz requirements query` (gitwizz `src/Gitwizz/RequirementContext.cs`). It reads
`gitwizz.requirement-query/v1` (the PR's changed symbols) and prints `gitwizz.requirement-input/v1`:

| Field | From the index |
|---|---|
| `links[].type` | `verified` for `confirmed` (a provider-verified commit link), `candidate` for everything else |
| `links[].source` | `commit-link`, `pr-link` or `commit-message`: the strongest evidence behind the link |
| `links[].freshness` | `stale` when the code changed since the newest linked commit, `unknown` when the symbol isn't in the current code |
| `links[].evidence` | at most 5 lines: relation, commit, file and lines (repository-relative) |
| `unavailable` | no index, truncated or shallow history, commits with unknown links, linked work items not exported (at most 20) |
| `analyzedCommit` | the last exported commit: gitwizz calls the answer stale when it isn't the target |

Only requested symbol ids are answered (at most 10 links each). A symbol whose id isn't in the history is looked up by
its `previousId` (a changed signature). The PR's own commits are usually not in the export yet: an answer never says
they were indexed. A query that isn't valid JSON or `gitwizz.requirement-query/v1` exits 1 with the reason on stderr.
