# Dependencies and test snapshots

[Documentation home](../README.md) · [CLI reference](../cli.md)

## SBOM inventory

`docwizz sbom .` writes `sbom.cdx.json` (or `docwizz sbom . output.json`): CycloneDX 1.6 from NuGet, npm, Maven and
Gradle **manifests only**.

- Direct declared dependencies with a package URL (`purl`) and their declared version. Ranges, wildcards and property
  references are kept as declared but left out of `version` and the purl.
- Project-to-project references appear in the dependency graph.
- Not included: transitive dependencies, lockfiles, licenses, vulnerabilities. No restore, build or network access.
  Output is deterministic.

`generate` writes the same inventory to `views/packages.md`, listing packages declared at several versions first.

When running `docwizz generate .`, its console report checks the existing requirements index and any `docs/.docwizz/ado-tests-*.json` snapshots. It prints whether each source is imported, missing or invalid and summarizes validated test snapshots. This is **status feedback only**: it does not prove test execution, technical test coverage or requirement verification, and it does not contact Azure DevOps. Import snapshots first with `docwizz tests import`.

### Offline Azure DevOps test snapshots

`docwizz tests import . alpha-tests.json` accepts both single-plan `ado.test-traceability/v1` and multi-plan `ado.test-traceability/v2` snapshots; v2 preserves explicit plan/suite/testcase assignments. It validates these references and stores the snapshot offline.

`docwizz tests import . alpha-tests.json` validates `ado.test-traceability/v1` exported by `ado testplan trace-export` and saves a normalized snapshot under `docs/.docwizz/ado-tests-<planId>.json`. The command reports explicit requirement-to-test-case associations. It does not infer relationships to code tests or assert test execution. The generator adds a separate `tests.md` page (and an HTML page with `--html`) showing explicit plan, requirement and test-case associations; it never asserts execution or coverage.
