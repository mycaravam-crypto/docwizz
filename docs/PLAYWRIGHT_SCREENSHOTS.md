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

Sign in using the opened browser, wait for an authenticated page to load, then close the browser to finish recording the state. The file stores cookies and localStorage data; **sessionStorage is not preserved**. DocWizz does not collect credentials or perform the login itself.

Add `.auth/` to your application's `.gitignore`. Treat the state file as a credential: restrict filesystem access, never upload it to issues or CI artifacts, rotate/revoke sessions if exposed, and refresh it when the session expires.

## Generate screenshots

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
| `screenshot skipped: ... would end the session` | Logout routes are never opened, so later routes stay signed in. |
| Only `/` is signed in, later routes are not | Fixed by carrying the session from route to route; if it persists, check whether the app keeps its token in sessionStorage (not saved by Playwright). |
| Pages captured half-loaded (spinners, empty tables) | Add `--screenshot-delay 1500` (milliseconds, up to 60000). |
| `screenshot skipped: ... HTTP 4xx/5xx` | The target route returned an HTTP error; verify application and permissions. |
| `N items lack docs; pass --ai` | A documentation-coverage notice, unrelated to screenshots; AI drafting is optional. |
| `N pages ... 0 changed` | Previously generated documentation pages did not change; it is **not** confirmation that screenshots were captured. |

### Known limitations

DocWizz uses a **fresh browser context per route**: the first is seeded with the saved storage state, each later one with the previous route's cookies and storage (in memory only; the state file is never modified), so refresh-token rotation does not sign later routes out. Each screenshot is taken once the network is idle (up to 5 s), after 400 ms plus `--screenshot-delay`, but does not execute application-specific navigation or data setup. Dynamic routes, HTTP errors and URL redirects are skipped. Login forms with a password field are detected; other login overlays that do not change the URL may not be; manually inspect the resulting images. If your app uses sessionStorage-only authentication, use an application/test-environment mechanism compatible with Playwright storage state.

For reproducible documentation in CI, provision test data, use a non-production environment, refresh the login state securely and review screenshots before publishing.
