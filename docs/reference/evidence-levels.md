# Evidence levels

[Documentation home](../README.md)

Generated documentation combines code facts with imported evidence: requirement history, work-item catalogs, ADO test
cases, declared project evidence, load reports. Each item keeps the level of its evidence, and docwizz never turns one
level into a stronger one. This page is the reference the other guides point to when they say "not proof of …".

| Level | Comes from | Shows | Does not show |
|---|---|---|---|
| **Detected** | source structure, signatures, dependencies and repository files, cited to `file:line` | what the code contains | intent |
| **Inferred** | a heuristic or a package alone (`(inferred)` in the pages) | a likely fact | certainty |
| **Explicitly linked** | an ADO requirement → test-case relationship, or a link declared in [project evidence](../guides/project-evidence.md) | that someone declared the relationship | that the test ran or passed |
| **Historically associated** | [requirement history](requirement-formats.md): a commit or pull request linked to a work item touched the code | what the code was changed for, then | that the current code implements the requirement |
| **Structurally linked** | test code calls, imports or constructs a symbol | that a test uses the code | code coverage, or that acceptance criteria are met |
| **Measured** | imported [load reports](../guides/load-reports.md), with capture time and target | numbers from one run | behaviour in another environment |
| **AI-drafted** | a self-hosted model, citing facts (🤖) | a starting point for writing | anything; it never closes a gap |

Rules that follow from this:

- A missing connection is never made up from name similarity.
- Every source keeps its provenance and scope: project, area path, test plan, snapshot time.
- "Not linked", "not assessed" and "unavailable" are kept apart: the absence of a link in the imported evidence is
  not evidence that something is missing.
- No combined verification or requirement-coverage rate is computed. A run status would need an imported result format
  with outcomes and timestamps, which none of the current imports carry.
- Missing or invalid optional evidence is reported, and never stops the code documentation from being generated.
- Generation stays offline and deterministic for identical inputs.
