# Screenshots and user guides

[Documentation home](../README.md) · [CLI reference](../cli.md)

docwizz can capture the frontend routes it detected from an application that is already running, and write
step-by-step user guides with a picture per step. Both are optional, use Playwright Chromium and need no AI. docwizz
never automates a login or handles passwords other than the test-account values you pass in a journey file.

## Prerequisites

- The application is reachable at a base URL, for example `http://localhost:5173` or a test environment.
- Playwright Chromium matching docwizz's Playwright version (1.63.0, in `src/DocWizz/DocWizz.csproj`) is installed for
  the same OS user that runs docwizz:

  ```bash
  npx playwright@1.63.0 install chromium
  # or, after building docwizz from source:
  pwsh ./src/DocWizz/bin/Release/net10.0/playwright.ps1 install chromium
  ```

- For signed-in pages: a dedicated, least-privilege test account with sanitized data.

Capture from a production build where you can: a dev server adds overlays (Vite's Vue Devtools button, error overlays)
to every page. For a Vite app, `vite build` and then `vite preview` serves the build and keeps the dev server's `/api`
proxy.

## Route screenshots

```bash
docwizz generate . --html --screenshots --base-url http://localhost:5173
```

Pictures go to `docs/screenshots/` and are linked from `frontend.md` and each route's page under `routes/`. A later
`generate` without `--screenshots` keeps the last captures linked; only `--screenshots` replaces them.

- Each route opens in a fresh browser context: anonymous by default, or signed in with `--storage-state` (below).
- Before each capture docwizz waits for the network to go idle (up to 5 s) plus 400 ms; `--screenshot-delay <ms>`
  adds more for slow pages.
- Routes of a hash-history router (`createWebHashHistory`, `createHashRouter`, `<HashRouter>`, Angular `useHash: true`
  or `withHashLocation()` in the routes file) are opened as `<base-url>/#/route`.
- Skipped with a warning: dynamic routes, `/logout` and similar routes that would end the session, router aliases that
  redirect to another route (the target is captured on its own), HTTP errors, other redirects, and signed-in pages that
  show a password field or whose API calls are answered 401 or 403.

### Signed in

Record a login once, from the application's repository (the directory you pass to `docwizz generate`):

```bash
npx playwright@1.63.0 codegen --save-storage=.auth/docwizz-state.json http://localhost:5173
docwizz generate . --html --screenshots --base-url http://localhost:5173 --storage-state .auth/docwizz-state.json
```

Sign in in the browser that opens, wait for a signed-in page, then close it. The state file is per origin, so record
it against the URL you capture from; its path is relative to the scanned directory. With a session, each route starts
from the previous route's cookies and storage (in memory; the file is never written), so apps that rotate refresh
tokens stay signed in.

Playwright saves cookies and localStorage but **not sessionStorage**. For an app that keeps its token there, add a
`sessionStorage` list next to `localStorage` in the origin's entry (same `{ "name", "value" }` shape); docwizz seeds
every page of that origin with it before the app's scripts run. A Playwright script can record it after signing in:

```js
const state = await context.storageState();
const sessionStorage = await page.evaluate(() => Object.entries(sessionStorage).map(([name, value]) => ({ name, value })));
const origin = new URL(page.url()).origin;
let entry = state.origins.find(o => o.origin === origin);
if (!entry) state.origins.push(entry = { origin, localStorage: [] });
entry.sessionStorage = sessionStorage;
require('fs').writeFileSync('.auth/docwizz-state.json', JSON.stringify(state), { mode: 0o600 });
```

**The state file holds session tokens.** Add `.auth/` to `.gitignore`, restrict access to it, never upload it to
issues or CI artifacts, and revoke the session if it leaks.

## User guides

`--journeys <dir>` writes user guides to `docs/user-guides/`, each linked from its route and its work-item page and
back. With `--html`, `docs/user-guides/manual.html` holds every guide on one printable page for the application's
users, without links into the code documentation. The quality page lists work items behind a route that have no guide.
The directory can hold two kinds of input.

### From journey files

A journey file describes one guide. docwizz plays it in the running application, takes a picture before each step
with the control outlined, and writes the guide. Nothing is guessed: a control that is missing or ambiguous fails the
generation.

```yaml
# journeys/01-sign-in.yaml
title: Sign in
intro: You need the account your administrator created for you.
result: The start page opens. You stay signed in until you close the browser.
requirement: 4711          # optional: AB#4711, or github#123 for a GitHub issue; links the guide and its requirement page
steps:
  - open: /login
  - fill: Email
    with: ${DOCWIZZ_USER}  # read from the environment, never printed
    note: Use the email address of your account, not a private one.
  - fill: Password
    with: ${DOCWIZZ_PASSWORD}
  - click: Sign in
```

```bash
docwizz generate . --html --journeys journeys --base-url http://localhost:5173
```

- **Steps:** `open` a path, `click` a button, link, tab, menu item or text by its name, `fill` a field by its label or
  placeholder. `css=<selector>` addresses a control that has no usable name. `note` says what the reader sees and why.
- **Order:** files run in file-name order in one browser session, so a sign-in journey signs the later ones in.
  `--storage-state` and `--screenshot-delay` work as for route screenshots.
- **Values:** `${NAME}` types that environment variable into the page; use it only for the test account's credentials.
- **Requirement:** an Azure Boards id (`4711`, `AB#4711`) or a GitHub issue (`github#123`, of the repository's
  github.com origin, or `github:owner/repo#123`). A bare `#123` is refused because it could mean either. Import the
  requirements ([Requirements](requirements.md)) before capturing: the guide then records the item's revision (ADO) or
  last update (GitHub) and is marked stale when the item changes. A guide captured without that is listed under
  **Requirement not tracked** on the user-guide index.
- **Without the application:** a later `generate` without `--journeys` keeps the captured guides with their capture
  date. A kept guide is marked stale once the source files behind the route it opens first have changed, or its
  requirement changed. A stale guide carries a banner, is listed under **Stale guides** on the user-guide index, and
  is left out of `manual.html` until it is captured again. To remove a
  guide, delete its journey file and capture again.

### From TestWizz evidence

docwizz also imports reviewed browser evidence that TestWizz recorded
from Azure DevOps test cases. The import is offline and never runs tests.

```bash
ado testplan export 12 --output ../ado-plan.json
testwizz journey . --url http://127.0.0.1:5173/orders \
  --workitem 4711 --plan ../ado-plan.json --case 512 \
  --steps journey.json --out ../journey-evidence
docwizz generate . --html --journeys ../journey-evidence
```

`--journeys` takes one evidence bundle or a directory of them. Only `testwizz.ui-evidence/v1` manifests whose steps all
passed are accepted; every PNG's SHA-256 is checked and image paths are confined before the pictures are copied to
`docs/user-guides/images/`. Missing, altered or invalid evidence fails the generation. Unlike guides from journey files,
these pages are removed by a later `generate` without `--journeys`.

A step's before-action picture can number several controls that are visible together (**1** Customer, **2**
Description, **3** Save). A number is the control's position in the reviewed journey, not an ADO step id, which is
printed separately. Each guide keeps the ADO work-item and test-case revisions, expected results and acceptance
criteria. Passed visibility checks are not acceptance verification: a guide is a draft for editorial and privacy review.

### Data safety

Capturing clicks and submits forms in the running application, and values typed into fields appear in later
pictures. Use a non-production environment, a disposable test account and synthetic data. Review and redact every
picture before publishing, and restrict access to the generated site.

## Troubleshooting

### "Executable doesn't exist" / `chromium_headless_shell-…`

```text
docwizz: screenshots unavailable: Executable doesn't exist at
C:\Users\<user>\AppData\Local\ms-playwright\chromium_headless_shell-...\chrome-headless-shell-win64\chrome-headless-shell.exe
```

docwizz could not launch the browser revision its .NET Playwright runtime expects. The rest of `generate` may still
finish, but no screenshots were taken.

1. Install Chromium with the matching Playwright version (see [Prerequisites](#prerequisites)), as the user that runs
   docwizz; in CI, in the same job. A different or latest Playwright browser is not a substitute.
2. Check the exact path from **your** error message, for example
   `Test-Path "$env:LOCALAPPDATA\ms-playwright\chromium_headless_shell-1243\chrome-headless-shell-win64\chrome-headless-shell.exe"`.
   The revision number changes with Playwright versions.
3. If `PLAYWRIGHT_BROWSERS_PATH` is set, it must point to the same browser cache for installation and capture.

The `bin/Debug/netX/playwright.ps1` path in Playwright's own error message is a placeholder, not your docwizz install.

### Messages

| Output | Meaning / next step |
| --- | --- |
| `screenshots: skipped N dynamic/unsupported route(s)` | parameterized routes such as `/customers/:id` are never captured |
| `screenshot skipped: ... redirected to ... (needs authentication?)` | check the stored login, origin and expiry; record the state again |
| `screenshot skipped: ... shows a login form (session not accepted?)` | the session was rejected; record the state again |
| `screenshot skipped: ... API answered 401/403 for /api/... (session not accepted?)` | the session expired or the account lacks the role |
| `screenshot skipped: ... would end the session` | logout routes are never opened, so later routes stay signed in |
| `screenshot skipped: ... HTTP 4xx/5xx` | the route returned an HTTP error |
| Every route shows the home page | the app uses hash routing but the router wasn't recognized (see [Route screenshots](#route-screenshots)) |
| Only `/` is signed in | the app probably keeps its token in sessionStorage: add it to the state file (see [Signed in](#signed-in)) |
| Pages captured half-loaded | add `--screenshot-delay 1500` |
| `N pages ... 0 changed` | no page text changed; it does not confirm that screenshots were taken |

A login overlay that has no password field and doesn't change the URL can't be recognized: check the pictures.
