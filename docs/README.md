# Documentation

[Project overview](../README.md) · [Getting started](getting-started.md) · [CLI reference](cli.md)

**Start with a task.** Tutorials are under `guides/`; exact contracts and data formats live in `reference/`; implementation details in `internals/`. The main CLI reference contains syntax, flags and exit codes—not parallel walkthroughs.

## Getting started and daily work

- [Getting started](getting-started.md) — setup, first documentation, initial checks
- [Generate documentation](guides/generation.md) — Markdown, HTML and optional AI drafting
- [Quality, architecture and CI](guides/quality.md) — checks, change impact, CI
- [Multi-project repositories](guides/multi-project.md) — scan scope and hierarchy
- [Coding agents and MCP](guides/agent-context.md) — bounded context and MCP tools
- [CLI reference](cli.md) — every command, option, precedence rule and exit code

## Features and evidence

- [Requirements and traceability](guides/requirements.md) — ADO work-item catalog, Git history and explicit test links
- [ADO evidence synchronization](guides/ado-sync.md) — offline evidence export and import
- [Project evidence](guides/project-evidence.md) — explicit requirement → architecture → test links
- [Domain discovery](guides/domain-discovery.md) — technical exploration and inferred areas
- [Product document workflow](guides/product-documents.md) — drafts, reviews and publishing
- [V-Modell-XT templates](guides/vmodell-xt-templates.md) — example structures and validation
- [IT operations concept](guides/it-operations.md) — sourced draft and limits
- [Screenshots](guides/screenshots.md) — authenticated capture and troubleshooting
- [Dependencies and test snapshots](guides/supply-chain.md) · [Load reports](guides/load-reports.md)

## Contracts and implementation

- [Requirement interchange formats](reference/requirement-formats.md) — provider and historical evidence semantics
- [Evidence provenance and limits](reference/evidence-levels.md) — observed, declared, historical, inferred
- [EDMX graph and links](reference/edmx.md) — versioned technical contracts
- [Signed publication](reference/publication.md) — signatures, independent approval and boundaries
- [Scanner distribution](reference/scanner-distribution.md) — package and action runtime contracts
- [Implementation architecture](internals/architecture.md) — scanners, data model, generators and tests
- [Product contract decision](internals/decisions/product-template-contract.md) — architecture decision
- [SW architecture template guide](reference/vmodell-xt/sw-architecture.md)
  · [SW specification](reference/vmodell-xt/sw-specification.md)
  · [System-element test specification](reference/vmodell-xt/system-element-test-specification.md)

## Historical evidence

- [Multi-project acceptance baseline](archive/multi-project-acceptance.md) — historical, point-in-time checklist
- [Benchmarks](../bench/README.md) — reproducible synthetic measurements

Active feature work is tracked in [GitHub Issues](https://github.com/mycaravam-crypto/docwizz/issues) and [Pull Requests](https://github.com/mycaravam-crypto/docwizz/pulls). Maintained pages use lowercase `kebab-case.md`; `README.md` is an index convention, and obsolete compatibility pages have been removed.

**Security:** generated application documentation, search indexes and imported work items may contain private requirements. Distribution and access to generated output are independent of the maintained project documentation here.
