# Roadmap

What DocWizz does today is in [README.md](README.md), and how it works is in [ARCHITECTURE.md](ARCHITECTURE.md).
This page lists what's next, in rough priority order. The rules from the start still hold: deterministic analysis
first, AI only as synthesis, never invent intent, keep the evidence, and say what is detected, inferred, AI-drafted
or human-written. AI runs on a self-hosted model only; code is never sent to a public service.

## Next

- **C# depth.**
  - `UseMiddleware<T>` pipeline order.
  - `AddHostedService<T>`.
  - Response types from `Produces`/`ProducesResponseType` and `TypedResults`.
  - Request body/parameter binding for minimal APIs without attributes.
- **Architecture risks.**
  - Coupling metrics per module (fan-in/fan-out, instability).
  - Entities exposed directly as API responses.
  - Controllers with business logic (high complexity in the api layer).
  - Unexpected packages in the domain layer.

## Later

- **Section-level AI.** Extend drafts from a summary to responsibilities, behaviour, side effects, error behaviour
  and usage, per symbol and per module. They would use the same inputs: facts marked by origin, the symbol's own
  source, and existing docs. They would keep per-sentence provenance to the facts they came from.
- **Incremental regeneration.** Regenerate only the pages whose inputs changed, using the node hashes and the
  impact map.
- **Navigation.** Backlinks from every component to the flows, endpoints and configuration keys that involve it,
  plus a search index.
- **Helm values and Kubernetes ConfigMaps** as configuration sources. Terraform/Bicep resources linked to the
  external systems they provision.

## Eventually

- HTML output.
- CI integration: a GitHub Action that comments `check --since` results on pull requests.
- More languages and frameworks: SQL (stored procedures, migrations), React/Angular, Java/Spring.
- ADR candidates, i.e. decisions the code implies (a new external system, a new layer dependency) proposed as
  drafts for a human to write.
