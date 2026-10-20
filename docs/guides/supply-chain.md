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
