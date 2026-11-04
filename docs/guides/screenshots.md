# Playwright screenshots: authenticated capture and troubleshooting

DocWizz captures **detected static frontend routes** from an application that is already running. Screenshots are optional and do not require AI. It uses Microsoft.Playwright (currently **1.63.0** in `src/DocWizz/DocWizz.csproj`). Install a matching Chromium revision for the DocWizz binary you run; installing an unrelated/latest Playwright browser is not a reliable substitute.

## Prerequisites

- Your web application is reachable at a base URL (for example, `http://localhost:5173` or an HTTPS test environment).
- Playwright Chromium is installed **on the same machine and for the same OS user** running DocWizz.
- For authenticated routes, use a dedicated, least-privilege test account with sanitized data.

With a matching Node.js Playwright version, install Chromium:

```powershell
npx playwright@1.63.0 install chromium
```

If you built DocWizz from source, prefer the browser installer bundled with its .NET build. From the DocWizz repository root:

```powershell
dotnet build src/DocWizz -c Release
pwsh ./src/DocWizz/bin/Release/net10.0/playwright.ps1 install chromium
```

Use the `Debug` path instead of `Release` if that is the configuration you actually built. For a packaged .NET tool, use the script distributed with that tool/build if available. The `bin/Debug/netX/playwright.ps1` path in Playwright's generic error message is a placeholder, not necessarily your DocWizz installation path.

## Save an interactive login

From the **web application repository** (the directory that you will pass to `docwizz generate`):

```powershell
New-Item -ItemType Directory -Force .auth | Out-Null
npx playwright@1.63.0 codegen --save-storage=.auth/docwizz-state.json https://example.test
```

Sign in using the opened browser, wait for an authenticated page to load, then close the browser to finish recording the state. The file stores cookies and localStorage data; **Playwright does not save sessionStorage**. DocWizz does not collect credentials or perform the login itself.

**Apps that keep their token in sessionStorage:** add a `sessionStorage` list next to `localStorage` in the origin's entry
(same `{ "name", "value" }` shape). DocWizz seeds every page of that origin with it before the app's scripts run, never
overwriting what the page already holds, and carries what the page leaves to the next route. A small Playwright script
can record it after signing in:

```js
const state = await context.storageState();
const sessionStorage = await page.evaluate(() => Object.entries(sessionStorage).map(([name, value]) => ({ name, value })));
const origin = new URL(page.url()).origin;
let entry = state.origins.find(o => o.origin === origin);
if (!entry) state.origins.push(entry = { origin, localStorage: [] });
entry.sessionStorage = sessionStorage;
require('fs').writeFileSync('.auth/docwizz-state.json', JSON.stringify(state), { mode: 0o600 });
```

Add `.auth/` to your application's `.gitignore`. Treat the state file as a credential: restrict filesystem access, never upload it to issues or CI artifacts, rotate/revoke sessions if exposed, and refresh it when the session expires.

## Generate screenshots

Capture from a production build where you can: a dev server adds its own overlays (Vite's Vue Devtools button, error
overlays) to every page. For a Vite app, `vite build` and then `vite preview` serves the build and reuses the dev
server's `/api` proxy. A state file is per origin, so record it against the URL you capture from.

```powershell
docwizz generate . --html --screenshots --base-url https://example.test --storage-state .auth/docwizz-state.json
```

The storage-state path is resolved relative to the scanned directory (`.` above), not relative to the DocWizz executable. Captured images go to `docs/screenshots/` and are linked from the generated frontend documentation.

## Windows: "Executable doesn't exist" / `chromium_headless_shell-...`

Typical output:

```text
docwizz: screenshots unavailable: Executable doesn't exist at
C:\Users\<user>\AppData\Local\ms-playwright\chromium_headless_shell-...\chrome-headless-shell-win64\chrome-headless-shell.exe
```

This means DocWizz could not launch the browser revision expected by **its .NET Playwright runtime**. Documentation generation may still finish; it does **not** mean the screenshot step succeeded.

1. Install Chromium with the matching Playwright version, or run the built .NET `playwright.ps1 install chromium` script shown above.
2. Re-run `generate`; ensure the browser was installed under the account executing DocWizz. If running in CI, install Chromium in that CI job.
3. If the error persists, check the precise executable path in the error message:

   ```powershell
   Test-Path "$env:LOCALAPPDATA\ms-playwright\chromium_headless_shell-1243\chrome-headless-shell-win64\chrome-headless-shell.exe"
   ```

   Replace `1243` with the revision from **your own error message**; the revision number can change with Playwright versions. A `False` result confirms that the executable is absent at that location.
4. Confirm that `PLAYWRIGHT_BROWSERS_PATH` (if set) points to the same browser cache used for installation and execution. Do not copy arbitrary browser binaries into the expected path.

## Diagnosing other messages

| Output | Meaning / next step |
| --- | --- |
| `screenshots: skipped N dynamic/unsupported route(s)` | DocWizz intentionally excludes parameterized or unsupported routes (for example `/customers/:id`). This is separate from browser installation. |
| `screenshot skipped: ... redirected to ... (needs authentication?)` | Check the stored login, target origin, authorization and expiry; recreate the state if necessary. |
| `screenshot skipped: ... shows a login form (session not accepted?)` | The page rendered a password field without redirecting: the session was rejected. Recreate the state file. |
| `screenshot skipped: ... API answered 401/403 for /api/... (session not accepted?)` | The page loaded, but its data calls were rejected: the session expired or the test account lacks the role. Recreate the state or use an account with access. |
| Every route shows the home page | The app uses hash routing (`/#/orders`) but the router was not detected as such; docwizz recognizes `createWebHashHistory`, `createHashRouter`, `<HashRouter>`, `useHash: true` and `withHashLocation()` in the file that declares the routes. |
| `screenshot skipped: ... would end the session` | Logout routes are never opened, so later routes stay signed in. |
| Only `/` is signed in, later routes are not | Fixed by carrying the session from route to route; if it persists, check whether the app keeps its token in sessionStorage: Playwright doesn't save it, add it to the file as above. |
| Pages captured half-loaded (spinners, empty tables) | Add `--screenshot-delay 1500` (milliseconds, up to 60000). |
| `screenshot skipped: ... HTTP 4xx/5xx` | The target route returned an HTTP error; verify application and permissions. |
| `N items lack docs; pass --ai` | A documentation-coverage notice, unrelated to screenshots; AI drafting is optional. |
| `N pages ... 0 changed` | Previously generated documentation pages did not change; it is **not** confirmation that screenshots were captured. |

### Known limitations

DocWizz uses a **fresh browser context per route**: the first is seeded with the saved storage state, each later one with the previous route's cookies and storage (in memory only; the state file is never modified), so refresh-token rotation does not sign later routes out. Each screenshot is taken once the network is idle (up to 5 s), after 400 ms plus `--screenshot-delay`, but does not execute application-specific navigation or data setup. Dynamic routes, HTTP errors and URL redirects are skipped. Login forms with a password field are detected; other login overlays that do not change the URL may not be; manually inspect the resulting images. sessionStorage listed in the state file is seeded into every page and carried the same way.

For reproducible documentation in CI, provision test data, use a non-production environment, refresh the login state securely and review screenshots before publishing.

## Screenshot-backed user-guide drafts from TestWizz

DocWizz also consumes **reviewed multi-step browser evidence** generated by TestWizz. This is separate from the existing `--screenshots` route capture. The import is offline, requires no AI, and never executes tests.

```bash
# Obtain the test-case steps from ado-devops, outside the application repository.
ado testplan export 12 --output ../ado-plan.json

# Review a small journey.json, with explicit step IDs, locators and assertions.
testwizz journey . --url http://127.0.0.1:5173/orders \
  --workitem 4711 --plan ../ado-plan.json --case 512 \
  --steps journey.json --out ../journey-evidence

# Generate user-guide draft pages and hashed PNG assets.
docwizz generate . --html --journeys ../journey-evidence
```

The new `--journeys` option accepts a single TestWizz evidence bundle directory or its parent containing multiple bundles. DocWizz accepts only `testwizz.ui-evidence/v1` manifests with passed per-step assertions, validates every PNG SHA-256 and restricts image paths before copying screenshots to `docs/user-guides/images/`. Guides appear under `docs/user-guides/`, are searchable, and are linked from matching route pages. If evidence is missing, altered or invalid, generation fails rather than claiming a verified guide.

### Multiple numbered callouts

When several reviewed Playwright action locators are visible in the same UI state, TestWizz captures a **before-action** image with all uniquely resolved, visible targets numbered in journey order (up to the existing six-step limit). For example, a form may show **1 Customer**, **2 Description**, **3 Save** on one screenshot. TestWizz determines positions from Playwright locator bounding boxes and draws temporary, non-interactive bubbles. Controls from earlier and later reviewed steps remain numbered whenever they are uniquely identifiable and visible. Hidden or ambiguous controls are omitted until they become available. It does not annotate every selectable DOM element; only reviewed action locators belong to the documentation task.

The evidence manifest stays at `testwizz.ui-evidence/v1` and adds optional `guideScreenshot`, `guideSha256`, and `callouts[]` to each step. The original `screenshot` and `sha256` remain the *after-action* assertion evidence. DocWizz validates **both** PNG hashes, the callout number-to-ADO-step mapping, bounded coordinates and paths, then publishes the annotated before-action images with numbered explanations. Existing evidence bundles without annotations continue to work.

Each picture is captured for the currently visible state and can repeat controls from any reviewed step that remain visible. Numbers are guide positions (1, 2, 3), not ADO step IDs; the ADO IDs are printed separately for traceability. Each bubble number must equal the **one-based position of its referenced action in the reviewed journey**, regardless of which screenshot displays it. A two-action journey therefore cannot have a bubble numbered 3. Verify bubble placement, accessibility, contrast and image privacy before publication. The bubble renderer does not use a vision model or OCR.
The generated guides retain ADO work-item and test-case revisions, actions, expected results and acceptance criteria. **Visibility assertions are not complete acceptance verification**; the selected work-item/test-case association is explicitly unverified until a separate ADO relationship is checked. Each guide is a draft requiring human editorial and privacy review. The first increment does not use local AI to invent or rewrite user instructions; TestWizz's existing local-AI UI proposal remains available to assist a human in preparing reviewed journey steps.

Screenshot capture may modify the running application through same-origin clicks and form submission. Use disposable non-production accounts and synthetic test data. Keep ADO exports, browser credentials and raw evidence out of public repositories. `--journeys` copies selected images into generated documentation, which may itself be published: **review and redact every image before publication**, and restrict access to the generated site. The option is explicit: a subsequent generation without `--journeys` removes the generated user-guide pages and their TestWizz-named screenshots, without deleting manual images.
