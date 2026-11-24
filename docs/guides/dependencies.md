# Dependencies

[Documentation home](../README.md) · [CLI reference](../cli.md)

Both commands read NuGet, npm, Maven and Gradle manifests. Nothing is installed, restored or resolved over the
network, except the restore, build and test steps you ask `remediate --validate` to run.

## SBOM inventory

```bash
docwizz sbom .                 # writes sbom.cdx.json
docwizz sbom . output.json
```

A CycloneDX 1.6 inventory of **direct declared dependencies**: one component per project, one per package and declared
version with its package URL (`purl`), and the project → package and project → project graph. Ranges, wildcards and
property references are kept as declared but left out of `version` and the purl. Not included: transitive
dependencies, lockfiles, licenses, vulnerabilities. The output is byte-identical for identical inputs.

`generate` writes the same inventory to `views/packages.md`, listing packages declared at several versions first.

## Package updates

`docwizz remediate` suggests package updates with the evidence for each. It never touches your working tree:

```console
$ docwizz remediate . --package Newtonsoft.Json --to 13.0.3 --validate
DEP-001  Newtonsoft.Json 12.0.3, 13.0.1 → 13.0.3  [major]  (nuget; requested)

Suggested patch (versions are managed centrally; apply with git apply):
  -    <PackageVersion Include="Newtonsoft.Json" Version="12.0.3" />
  +    <PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />

Impact (static):
  - projects: src/Api/Api.csproj, src/Worker/Worker.csproj; through project references: tests/Api.Tests/Api.Tests.csproj  [detected]
  - 1 symbols import the package's namespaces (1 public): Api.Codec (src/Api/Codec.cs:3)  [inferred]
  - not assessed: APIs removed or changed between 12.0.3/13.0.1 and 13.0.3

Validation (executed in a temporary copy; passing restore, build and tests is evidence, not proof of behavioral compatibility):
  - restore: pass  `dotnet restore Demo.slnx`  3.2s
  - build: pass  `dotnet build Demo.slnx --no-restore`  5.5s
  - test: pass, 1 passed, 0 failed  `dotnet test Demo.slnx --no-build`  2.2s
Status: validated   Confidence: high
```

- **Targets:** `--package <name> --to <version>`, else `remediation.targets` in `docwizz.yaml`, plus NuGet packages
  declared at different versions across projects.
- **Suggestion:** a copy-paste `dotnet add package` command, or a patch when versions are managed centrally. npm,
  Maven and Gradle updates are reported without one.
- **Impact:** referencing projects, types that import the package's namespaces, the endpoints whose flows pass through
  them, and their linked tests.
- **`--validate`** copies the repository to a temporary folder, applies the change and runs the
  [configured steps](../configuration.md#remediation) there (default: `dotnet` restore, build, test). This executes
  repository code.
- **Status:** `unvalidated`, `validated`, `potentially-breaking` (a step failed) or `not-actionable`. A major update
  without validation is always low confidence.
- **`--since <ref>`** says whether each update touches only code that changed since `<ref>`.
