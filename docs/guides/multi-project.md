# Multi-project repositories

[Documentation home](../README.md) · [CLI reference](../cli.md)

## Selecting and scanning projects

### Discover

`docwizz discover .` lists project manifests, solution memberships and unresolved references; `--format json` gives a
deterministic inventory. It runs no language scanners, build tools or AI, and ignores `scope:` so you always see
everything available.

### Select projects

```bash
docwizz setup .                   # new ambiguous repository in a terminal: offers selection
docwizz setup . --select-projects # select or reselect (terminal required)
docwizz setup . --all-projects    # clear the saved scope, keep the rest of docwizz.yaml
docwizz setup . --yes             # scripts and CI: never prompt
```

- **When it prompts:** only on a new repository with several solutions or standalone projects, when stdin and stdout
  are terminals and `CI` is unset. Existing configs never prompt unexpectedly. `--select-projects` fails fast without a
  terminal or with `CI` set.
- **The wizard** lists projects by path, role, solutions and reference count. Type numbers to toggle, `s1`/`s2` to
  toggle a solution, `a` all, `n` none, `d` done, `q` cancel (several commands separated by commas or spaces). It
  starts with root-level solutions selected and test projects excluded. Invalid selections show a message and can be
  corrected; cancelling changes nothing and aborts before the scan.
- **What it writes:** only the top-level `scope:` block. Other settings, comments and whitespace outside it are kept;
  comments inside it are not.
- **Stages** become `discover → select → scan → config → …`. Config still runs after the scan, so inferred layers and
  test globs are based on the selected code.

### Scope

```yaml
scope:
  projects:
    include: ["Cargo-Core/Cargo-Core.csproj", "FileAPI/FileAPI.csproj"]
    references: metadata          # metadata | include | exclude
    test_links: true
  unassigned_sources: exclude     # source files no project owns
  repository_files: include       # repository-wide deployment, CI and configuration descriptors
```

- **Paths** are manifest paths relative to the scan root, case-sensitive, with `/`; copy them from
  `docwizz discover . --format json`. No globs, absolute paths or `..`. An invalid path is an error, never a fallback
  to all projects. Several manifests in one directory must be selected together.
- **`references`:** `metadata` (default) keeps referenced, unselected projects as labeled dependency-only stubs, without
  code or packages; `include` scans every reachable referenced project; `exclude` omits them and lists each omitted
  reference in the scope report.
- **`test_links: true`** also scans test projects that reference a selected project, for traceability only; test code
  stays out of production docs and coverage.
- **`unassigned_sources`** controls loose source files. A root-level manifest owns only its own language's files (C#
  for `.csproj`, JS/TS/Vue for `package.json`, Java for Maven/Gradle).
- **`repository_files: include`** (default) keeps Docker/Compose, Kubernetes, Terraform, CI and configuration
  descriptors for the system view, except those of an excluded project.
- The scope applies **before** scanning, to every command except `discover`. `exclude:` still wins. `docs/index.md`
  discloses the requested and effective selection, stubs, omitted and missing references.
- Changing the scope makes `context` and `mcp` rescan and requires `generate` before a baseline `diff`. New project
  references or moved projects also need a fresh `generate`.

Without `scope:`, the whole repository is scanned as before.

### Generated hierarchy

With projects or solutions in the model, `generate` writes `docs/index.md` → `docs/solutions/*.md` →
`docs/projects/*.md` → `docs/modules/*.md` → components; `--html` adds a nested sidebar, breadcrumbs and search entries.

- A project page shows its path, role, SDK and frameworks, solutions, source-file count, modules, components, linked
  tests (not coverage), project references and direct packages. Metadata-only projects get a disclosure-only page.
- A shared project has one page, linked from every solution that contains it. Projects without a solution and
  unassigned modules get their own branch.
- File names combine a readable slug with a stable suffix; don't hard-code them. Generated project and solution pages
  are removed on a scope change; hand-written pages are never touched.

### Limits

Classic non-SDK ASP.NET MVC projects are inventoried, but conventional `MapRoute` routing can't be fully inferred
statically; `api.md` warns when such a project is present. The acceptance matrix and migration notes are in
[MULTI_PROJECT_ACCEPTANCE.md](../quality/multi-project-acceptance.md); `bash scripts/benchmark-multi-project.sh` measures full
versus scoped scans.
