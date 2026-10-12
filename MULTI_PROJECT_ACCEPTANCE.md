# Multi-Project Acceptance – Epic #124 (P0–P4)

This is the traceable final acceptance checklist for discovery, explicit scope, setup selection, hierarchical documentation and regression/safety validation. **No runtime MSBuild/restore, arbitrary repository scripts or AI are executed by discovery.** CI status must be checked on the actual PR head before the Epic is closed.

| Epic acceptance | Evidence and limits |
|---|---|
| 1. Deterministic inventory: multiple solutions, at least four projects, test and orphan | `MultiProjectAcceptanceJourneyTests.Discovery_matches_versioned_golden_and_never_changes_the_repo`: two solutions, eight projects including tests and legacy manifests, cross-solution membership and orphan, file in `tests/DocWizz.Tests/Golden/mixed-project-inventory.txt`. `DiscoveryJourneyTests` covers JSON and missing solution entries. |
| 2. Select three of eight and reproduce selection | `SetupProjectSelectionTests` covers toggles, solutions, cancellation, reuse and YAML-only writes. `SetupProjectWizardJourneyTests` covers `--select-projects`, `--yes`, `--all-projects`; the new acceptance fixture persists three selected manifests and regenerates deterministically. |
| 3. Scope affects scan, APIs, search, modules, coverage | `ProjectScopeJourneyTests`, `MultiProjectAcceptanceJourneyTests`: excludes other C#, npm/JS and Java symbols from scoped source model/search and package inventory. Scoped scan is before language parsers, not output-only. |
| 4. `references: metadata` does not disclose excluded code | `ProjectScopeJourneyTests.Metadata_stubs_preserve_edges_without_scanning_excluded_code`, `ProjectNavigationJourneyTests.Scoped_regeneration_keeps_metadata_stubs_and_removes_stale_generated_pages`, new mixed fixture. |
| 5. System → Solution → Project → Module navigation | `ProjectNavigationJourneyTests` validates Markdown pages, HTML sidebar, breadcrumbs, search and links, and legacy module URLs. |
| 6. Same names and shared projects are unambiguous | `ProjectDiscoveryTests.Shared_projects_and_same_named_projects_have_distinct_ids`, `ProjectNavigationJourneyTests.Generates_unique_project_and_solution_pages_and_cross_links`. |
| 7. Tests remain evidence, not production coverage | `ProjectScopeJourneyTests`, `ProjectNavigationJourneyTests.Project_overview_lists_structurally_linked_test_evidence` and new mixed fixture. |
| 8. Never prompt in CI or redirected input; no scope means legacy behavior | `SetupProjectSelectionTests.Tty_policy_never_prompts_when_CI_is_set`, `SetupProjectWizardJourneyTests`, `ProjectScopeTests.Absence_of_scope_preserves_all_candidate_files`. |
| 9. Invalid paths, missing refs, symlinks, root escape and regeneration | `ProjectScopeTests`, `ProjectDiscoveryTests`, `DiscoveryBoundaryTests`; no symlinked or external files are accepted. Scope fingerprint and generated-page cleanup are covered in Journey tests. |
| 10. Legacy ASP.NET MVC inventory with honest endpoint limitations | `MultiProjectAcceptanceJourneyTests` exercises a non-SDK .NET Framework 4.7.2 `.csproj` without building; `api.md` warns that conventional `MapRoute` MVC endpoints cannot be treated as complete runtime routing inventory. Full MVC5 route analysis is explicitly out of scope. |
| 11. CI, deterministic non-AI and golden file | Focused/Component/Journey/E2E and Golden inventory, mixed scoped Markdown/HTML/model snapshot on rerun; existing generator cleanup tests preserve hand-authored files. The synthetic `ProjectHierarchyIndexTests` asserts one ownership lookup per eligible model node, independent of project count. |
| 12. User documentation and migration | `README.md`, `CLI.md`, `ARCHITECTURE.md`, and this acceptance checklist document scope/reference modes, precedence, prompt defaults, output hierarchy, partial legacy routing and limits. |

## Repeatable performance sample

Linux + .NET 10:

```bash
bash scripts/benchmark-multi-project.sh
PROJECTS=40 SOURCES_PER_PROJECT=20 RUNS=5 bash scripts/benchmark-multi-project.sh
```

The script generates a disposable synthetic C# repository, builds DocWizz **once**, warms each mode, then prints CSV wall-clock samples and medians for whole-repo versus a three-project scope. The CI `test/performance-baseline` job records those values in the job summary. Numbers are environment-dependent and intentionally **not** a gate: the direct operation-count regression detects asymptotic regressions deterministically.

## Compatibility and migration

- Without `scope:`, full-repository behavior is unchanged. `discover` inventories allowed manifests without executing code and ignores a saved selection.
- With `scope:`, paths are relative to the scan root, valid only inside `.gitignore` and `exclude:` boundaries, and loaded before scanning. Existing YAML settings remain preserved when choosing projects interactively.
- `references: metadata` produces dependency-only stubs; `include` traverses project references; `exclude` reports removed edges rather than claiming a complete graph. `test_links` adds relevant tests as traceability, not production source.
- Generated `solutions/` and `projects/` pages use stable path-derived filenames. Older `modules/` URLs remain. Generated pages are cleaned on scope changes; non-generated notes remain untouched.
- Old non-SDK .NET Framework MVC manifests appear in inventory. Conventional routing requires runtime context and **cannot** be inferred exhaustively; check the warning in `api.md`.
- A saved-model fingerprint captures scope configuration, not every change in manifest contents. Regenerate after changing manifest references.
- The intermittent Git fixture failure [#133](https://github.com/mycaravam-crypto/docwizz/issues/133) has additional diagnostics and an isolated Journey regression, but **its root cause has not been proven**. Track any recurrence separately from functional acceptance.

## Final review gate

Before marking the twelve Epic checkboxes done, require a merged PR with green `focused`, `journey`, `end-to-end`, `performance-baseline`, `docwizz` quality and `security` jobs on the current commit. Do not claim local green tests when .NET 10 is unavailable locally.
