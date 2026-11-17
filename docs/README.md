# Documentation

[Project overview](../README.md) · [Getting started](getting-started.md) · [CLI reference](cli.md) · [Configuration](configuration.md)

Guides walk through one task each; reference pages define formats and rules; internals describe the implementation.
Every topic has one home, and other pages link to it.

## Start

- [Getting started](getting-started.md): install, first `setup`, what its status means
- [Configuration](configuration.md): `docwizz.yaml`, profiles, architecture and security rules, AI
- [CLI reference](cli.md): every command, option, environment variable and exit code

## Guides

- [Generating documentation](guides/generation.md): pages, HTML, AI drafts and ratings, release history
- [Quality checks and change review](guides/quality.md): gaps, architecture, the CI gate, `diff`
- [Multi-project repositories](guides/multi-project.md): discovery, project selection, scope
- [Coding agents](guides/agent-context.md): context packages and the MCP server
- [Screenshots and user guides](guides/screenshots.md): route screenshots, journeys, troubleshooting
- [Requirements and traceability](guides/requirements.md): work-item catalogs, history links, ADO test cases
- [Domain discovery](guides/domain-discovery.md): follow code and requirements from one start point
- [Dependencies](guides/dependencies.md): SBOM and package updates
- [Load-test reports](guides/load-reports.md): measured numbers per endpoint
- [Product documents](guides/product-documents.md): drafts, checks and document control
  - [V-Modell XT templates](guides/vmodell-xt-templates.md) · [IT operations concept](guides/it-operations.md)
- [Project evidence](guides/project-evidence.md): declared requirement → architecture → test links

## Reference

- [Evidence levels](reference/evidence-levels.md): what each kind of evidence shows, and what it doesn't
- [Requirement formats](reference/requirement-formats.md): history exports, link rules, the index
- [Signed publication](reference/publication.md): DOCX/PDF release with independent approvals
- [EDMX analysis](reference/edmx.md): EF6 inventory, graph metrics, code links
- [Scanner distribution](reference/scanner-distribution.md): packaging and the GitHub Action's scanner payload
- V-Modell XT filling guides: [SW architecture](reference/vmodell-xt/sw-architecture.md) ·
  [SW specification](reference/vmodell-xt/sw-specification.md) ·
  [system-element test specification](reference/vmodell-xt/system-element-test-specification.md)

## Internals

- [How docwizz works](internals/architecture.md): scanners, the code model, analysis, generation, tests
- [Product template decision](internals/decisions/product-template-contract.md)
- [Benchmarks](../bench/README.md)

Work in progress is tracked in [GitHub Issues](https://github.com/mycaravam-crypto/docwizz/issues).

**Generated output is not public by default.** Generated documentation, search indexes and imported work items can
contain internal requirements; decide separately who may see them.
