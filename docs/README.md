# Documentation

[Project overview](../README.md) · [CLI reference](cli.md) · [Implementation](architecture.md)

Start with a task, then follow its guide. The CLI reference is the authoritative command/option list; the implementation notes explain how DocWizz works internally. All user-facing filenames use lowercase `kebab-case.md` (GitHub's `README.md` is the index exception).

## Start here

| Task | Entry point |
|---|---|
| Set up DocWizz and generate the first documentation | [Getting started](getting-started.md) |
| Look up a command, flag or exit code | [CLI reference](cli.md) |
| Set up a multi-project scan | [CLI: multi-project repositories](cli.md#multi-project-repositories) |
| Capture application screenshots | [Playwright screenshots](PLAYWRIGHT_SCREENSHOTS.md) |

## Understand a system

| Goal | Guide |
|---|---|
| Map work items to code and ADO test evidence | [Area-path requirements](area-requirements.md) |
| Understand imported commit / PR requirement context | [Requirement context](requirement-context.md) |
| Manage manually declared requirement → architecture → test evidence | [Project evidence](project-evidence.md) |
| Explore code and candidate business areas | [Domain discovery](domain-discovery.md) |
| Interpret observed, linked and inferred evidence | [Evidence-driven generation](evidence-driven-generation.md) |
| Inspect legacy Entity Framework models | [EDMX analysis](EDMX.md) |

**Evidence boundary:** a work-item link, test-case association or structural test-code reference is not proof of implementation, test execution or acceptance.

## Create and publish documents

| Task | Guide |
|---|---|
| Generate a sourced operations-concept draft | [IT operations concept](IT_OPERATIONS.md) |
| Draft using illustrative V-Modell-XT product templates | [V-Modell-XT templates](vmodell-xt-templates.md) |
| Add metadata, revision history and external approvals to drafts | [Document control](document-control.md) |
| Publish DOCX/PDF from independently signed approvals | [Controlled publication](document-publication.md) |

## Internals and maintained records

- [Implementation architecture](architecture.md) — code model, scanner contracts, generation, tests
- [Product template contract (ADR-0001)](adr/0001-product-template-contract.md) — rationale and conventions
- [Multi-project acceptance baseline](quality/multi-project-acceptance.md) — historical acceptance evidence; not a live specification
- [Benchmarks](../bench/README.md) — measurements and reproducible fixtures

Feature proposals and outstanding work live in [Issues](https://github.com/mycaravam-crypto/docwizz/issues) and [Pull Requests](https://github.com/mycaravam-crypto/docwizz/pulls). Generated application documentation is a separate output owned by `docwizz generate`, not the maintained sources in this repository.
