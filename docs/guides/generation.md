# Generating documentation

[Documentation home](../README.md) · [CLI reference](../cli.md)

## Generate Markdown and HTML

```bash
docwizz generate .                                         # Markdown in ./docs
docwizz generate . site --html                             # Markdown and HTML in ./site
docwizz generate . site --refresh-html-on-version-change   # re-render all HTML after a docwizz upgrade
```

Only pages whose content changed are rewritten, and the summary counts them. Every HTML page names the docwizz version
in its footer; `--refresh-html-on-version-change` (implies `--html`) rewrites all HTML when
`<out>/.docwizz/html-version.txt` differs from the running version. On an interactive terminal a live progress bar
on stderr starts at once and shows what each stage is doing (files parsed; AI drafts written, the estimated time left and the item being drafted) with the elapsed time,
leaving a ✓ line with timing and a short summary per finished stage; `--progress` forces plain one-line-per-stage
progress in CI logs (`NO_COLOR` turns colors off). The result on stdout starts with the one-line summary scripts match
(`N pages → <out> (commit …): N changed`), followed by a report: what was scanned, coverage and doc quality, gaps and
architecture findings with the page to open, and where the time went. See [Generated vs. your files](#generated-vs-your-files) for what you
may edit.

**Route screenshots** capture detected static frontend routes from an already running app with Playwright Chromium
(`npx playwright@1.63.0 install chromium`):

```bash
docwizz generate . --html --screenshots --base-url http://localhost:5173
npx playwright codegen --save-storage=.auth/docwizz-state.json http://localhost:5173   # once: sign in by hand
docwizz generate . --html --screenshots --base-url http://localhost:5173 --storage-state .auth/docwizz-state.json
```

- Each route gets a fresh, isolated browser context: anonymous by default, or initialized from the saved storage state
  (cookies and localStorage, plus `sessionStorage` entries you add to an origin in the file, for apps that keep their
  token there; see the troubleshooting guide). docwizz never automates login or handles passwords.
- With a session, each route after the first starts from the previous route's cookies and storage (kept in memory; the
  file is never written), so apps that rotate refresh tokens stay signed in. Routes such as `/logout` are skipped.
- Before each capture docwizz waits for the network to go idle (up to 5 s) plus 400 ms; `--screenshot-delay <ms>`
  adds more for slow pages.
- `--storage-state` paths are relative to the scanned directory.
- Routes of a hash-history router (`createWebHashHistory`, `createHashRouter`, `<HashRouter>`, Angular `useHash: true` /
  `withHashLocation()` in the routes file) are opened as `<base-url>/#/route`.
- A route that redirects to another of the app's routes (a router alias such as `{ path: '/erp', redirect: '/icd-schema' }`)
  is skipped as an alias; the target is captured on its own.
- Dynamic routes, HTTP errors (400+), other URL redirects, expired sessions and signed-in routes that show a password field
  (a login form without a redirect) are skipped with a warning. With a session, a route whose API calls (fetch/XHR)
  are answered 401 or 403 is skipped too: SPA servers answer every page with 200, so the API is where a rejected
  session shows. API 404s are not treated as failures. A login overlay without a password field can't be
  recognized.
- Captures go to `docs/screenshots/`, linked from `frontend.md` and the route's page under `routes/`. A later `generate` without `--screenshots` keeps the
  last captures linked (the summary says so); only `--screenshots` replaces them. **The storage-state file holds session tokens:** keep
  it out of Git, restrict access, and use a low-privilege test account with sanitized data.

For Windows Chromium installation, missing headless-shell executables, authenticated login steps and troubleshooting,
see [Playwright screenshot troubleshooting](../PLAYWRIGHT_SCREENSHOTS.md).

## Optional AI-assisted documentation

```bash
docwizz generate . --ai
docwizz setup . --ai
docwizz generate . --ai-ratings        # rate written docs without drafting
docwizz generate . --ai --ai-ratings   # explicitly request both
```

`--ai` drafts missing summaries and module overviews to start writing from. It is off unless passed, including in
`setup`. The model must be **self-hosted** on a loopback or private address: Ollama (default) or any OpenAI-compatible
server (vLLM, NVIDIA NIM, Nutanix Enterprise AI, a LiteLLM gateway). Cloud models and public hosts are refused and
proxies are bypassed. Drafts are marked 🤖, cite their facts, fill only empty sections and **never** close a gap, raise
coverage or pass a check. Drafts are cached by code hash; without `--ai`, no new drafts are requested. More:
[README: AI drafts](../../README.md#draft-missing-docs-with-a-local-ai).

`--ai-ratings` independently enables advisory 1–5 usefulness ratings of written docs in `quality.md` for `generate`
and `setup`. Ratings never affect coverage, doc quality or `check`. They can dominate a cold run, so `--ai` alone
no longer requests them. Cached ratings are displayed without either flag; new ratings are requested only for
missing entries keyed by symbol, documentation/code hash, provider and model. Each result is saved atomically as
it completes, so interruption preserves completed ratings. `--progress` reports the item count and estimates time
remaining from completed-request throughput.

```yaml
ai:
  provider: openai-compatible   # ollama (default) or openai-compatible
  endpoint: http://10.0.0.5:8000/v1
  model: qwen3-coder-next
  api_key_env: DOCWIZZ_AI_KEY   # optional: the variable that holds the key
```

```bash
DOCWIZZ_AI_KEY=... docwizz generate . --ai
```

The key is read only from the variable `api_key_env` names; `docwizz.yaml` refuses an `api_key` setting and the key is
never printed. If the variable is named but empty, `--ai` is skipped with a message. `DOCWIZZ_MODEL` overrides
`model`; for Ollama, `OLLAMA_HOST` overrides `endpoint`. Drafts are cached per provider and model. Ollama requests ask
for a 16,384-token context (`num_ctx`): Ollama's default (often 4,096) would silently cut longer prompts.

#### Requirements in drafts

```bash
docwizz requirements import . history.json work-items/   # once (see Requirement history)
docwizz generate . --ai --requirements
```

`--requirements` gives each symbol's prompt its linked work items from the requirement index, as `businessContext`:
at most `ai.requirements.max` items (default 3) and `ai.requirements.budget` characters (default 1200), confirmed before
candidate, unchanged since before changed since, newest commit first. Criteria are cut first, then whole items; the
facts say what was left out. Work items that were linked but not exported are never sent.

- The text is marked as untrusted data, not instructions, and stays in the facts; the instructions never contain it.
- Two more sections come back: **intent** (what a requirement asked for) and **deviation** (where the source differs).
  Each sentence must cite a `WI-<id>`, and gets its status appended: `[WI-12 confirmed]`, `[WI-7 candidate, code
  changed since]`.
- A **deviation** must also cite the source and quote, in backticks, the code it is about: `` (`password.Length >= 8`) ``.
  It is kept only when every quote occurs in the symbol's source (whitespace ignored). A deviation the model can't
  point to in the code is dropped.
- The code sections (summary, behaviour, …) rest on code: work-item citations are removed from them, and a sentence
  that cites only a work item is dropped. A sentence claiming the code satisfies or implements a requirement, or
  that anything is approved or certified, is dropped wherever it is.
- A requirements draft is cached under its own key (symbol, body hash and a hash of the selected context), so a new
  revision or link re-drafts that symbol only. Without `--requirements`, prompts, cache keys and pages are exactly as
  before, and requirements drafts are never shown.

```yaml
ai:
  requirements: { max: 3, budget: 1200 }   # work items per symbol, characters of their text
```
