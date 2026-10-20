# Getting started

Use this short path to configure DocWizz and generate source-backed documentation. For every CLI flag and exit code see the [CLI reference](cli.md).

## First run

```bash
docwizz setup .
docwizz generate . --html
docwizz check .
```

`setup` discovers the repository, writes `docwizz.yaml` when missing, scans source and runs its initial checks. It does **not** execute application tests or builds, and does not use AI unless requested. A finding is not necessarily a tool failure.

Open `docs/index.html` or `docs/index.md` in the scanned repository (the default output directory). Generated pages are derived from the source model; read the code for authoritative behavior before modifying it.

## After the first run

| Need | Next step |
|---|---|
| Adjust architecture rules or quality thresholds | Edit `docwizz.yaml`; review [CLI profiles and checks](cli.md) |
| Scope a large solution to selected projects | [Multi-project setup](cli.md#multi-project-repositories) |
| Compare code or architecture changes | `docwizz diff .`, then `docwizz check . --since <base>` |
| Include Azure DevOps work items and tests | [Area-path requirements](area-requirements.md) |
| Draft controlled product documents | [V-Modell-XT templates](vmodell-xt-templates.md) |
| Capture frontend pages behind a login | [Playwright screenshots](PLAYWRIGHT_SCREENSHOTS.md) |

## Important boundaries

- Source-derived documentation is **not** evidence that a requirement was implemented or verified.
- Imported work items and generated HTML/search indexes may contain confidential content; control their distribution.
- `docwizz generate` updates generator-owned pages; never hand-edit files that are marked as generated.
- AI drafting, screenshot capture and external evidence imports are opt-in and have separate setup steps.
