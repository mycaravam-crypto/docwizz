# Generating documentation

[Documentation home](../README.md) · [CLI reference](../cli.md) · [Configuration](../configuration.md)

```bash
docwizz generate .                                         # Markdown in ./docs
docwizz generate . site --html                             # Markdown and HTML in ./site
docwizz generate . site --refresh-html-on-version-change   # re-render all HTML after a docwizz upgrade
```

## What you get

- `index`, `architecture`, `api`, `frontend` and `quality` pages; one page per module (`modules/`) and per frontend
  route (`routes/`); `views/` for context, containers, components, data, packages and deployment. In a repository
  with several projects, the index leads through solutions and projects first ([multi-project](multi-project.md)).
- With an imported requirement index or work-item catalog, one page per work item (`requirements/`), linked both ways
  with routes and code ([requirements](requirements.md)); with imported ADO test cases, `tests.md`.
- With [screenshots or user guides](screenshots.md), pictures on the route pages and `user-guides/`.
- **HTML** (`--html`): an offline site with sidebar, search (`/`), outline and dark mode. Open `docs/index.html`.
  Mermaid diagrams load from a pinned CDN. Every page names the docwizz version in its footer.
- **Machine-readable:** `docs/.docwizz/documentation.json` lists every item's required sections, where each comes from
  (`written`, `fact`, `inferred`, `ai`) and its evidence (`file:line`). `search.json` indexes components, endpoints,
  routes, configuration keys and modules.

Only pages whose content changed are rewritten. Pages you write yourself (for example in `docs/architecture/`) are
linked, never overwritten; see [generated vs. your files](../cli.md#generated-vs-your-files). After upgrading docwizz,
`--refresh-html-on-version-change` rewrites every HTML page.

The first line on stdout is the stable one-line result for scripts (`N pages → <out> (commit …): N changed`). The
report below it says what was scanned, coverage and doc quality, gaps and findings with the page to open, which
imported evidence (requirements, ADO test snapshots) is present and valid, and where the time went. On a terminal a
live progress bar runs on stderr; `--progress` forces plain one-line-per-stage progress in CI logs.

## AI drafts

```bash
ollama pull qwen2.5-coder:7b
docwizz ai check .        # one synthetic request, no repository code; exit 0 if the model answers
docwizz generate . --ai
```

A self-hosted model drafts summaries, behaviour, side effects, errors and usage for undocumented items, plus an
overview for each module (folder) of three or more members. Server and model: [AI configuration](../configuration.md#ai).

- **Local only.** The server must resolve to a loopback or private address; proxies are bypassed and Ollama `…-cloud`
  models are refused.
- **Grounded.** The model gets each item's facts and its own source lines, never the repository. Every sentence must
  cite the facts it rests on; sentences that cite nothing are dropped.
- **Never authoritative.** Drafts are marked 🤖, fill only empty sections and never close a gap, raise coverage or pass
  a check.
- **Sent once.** Drafts are cached in `docs/.docwizz/ai-cache.json` by code hash, provider and model, including answers
  that had nothing usable, so a later `--ai` run sends only changed code. Without `--ai`, cached drafts are shown and no
  new ones are requested. Commit the cache to share it.

### Ratings

```bash
docwizz generate . --ai-ratings          # rate written docs, no drafts
docwizz generate . --ai --ai-ratings     # both
```

`--ai-ratings` rates written docs with an advisory 1–5 usefulness score in `quality.md`. Ratings never affect
coverage, doc quality or `check`. They can dominate a cold run with a local model, so they are off unless requested.
They are cached in `docs/.docwizz/ai-assessments.json` by symbol, documentation and code hash, provider and model, and
each is saved as it arrives, so an interrupted run keeps what it finished. Cached ratings are shown without either
flag. `--progress` shows the item count and the estimated time left.

### Requirements in drafts

```bash
docwizz requirements import . history.json work-items/   # once: see Requirements and traceability
docwizz generate . --ai --requirements
```

`--requirements` adds each symbol's linked work items to its prompt: at most `ai.requirements.max` items (default 3)
and `ai.requirements.budget` characters (default 1200), confirmed before candidate, unchanged before changed since,
newest commit first. Criteria are cut first, then whole items; the facts say what was left out.

- Work-item text is marked as untrusted data, never instructions.
- Two more sections come back: **intent** (what a requirement asked for) and **deviation** (where the code differs).
  Each sentence must cite a `WI-<id>` and gets its status appended: `[WI-12 confirmed]`, `[WI-7 candidate, code
  changed since]`.
- A **deviation** must quote, in backticks, the code it is about (`` `password.Length >= 8` ``) and is kept only when
  the quote occurs in the symbol's source.
- The code sections rest on code: work-item citations are removed from them. A sentence claiming the code satisfies a
  requirement, or that anything is approved or certified, is dropped wherever it is.
- These drafts are cached under their own key (symbol, body hash, selected context), so a new work-item revision
  re-drafts only that symbol. Without `--requirements`, prompts, cache keys and pages are unchanged.

## Release history

With a [GitWizz](https://github.com/mycaravam-crypto/gitwizz) version history export in
`docs/architecture/versions.json`, `generate` adds a version history page to the Markdown, HTML navigation and search:

```bash
gitwizz version-history export --target main -o docs/architecture/versions.json
docwizz generate . --html
```

Without the file nothing changes. The release history is separate from the revision history of
[product documents](product-documents.md#document-control).
