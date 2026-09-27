# Roadmap

What DocWizz does today is in [README.md](README.md), and how it works is in [ARCHITECTURE.md](ARCHITECTURE.md).
This page lists what's next, in rough priority order. The rules from the start still hold: deterministic analysis
first, AI only as synthesis, never invent intent, keep the evidence, and say what is detected, inferred, AI-drafted
or human-written. AI runs on a self-hosted model only; code is never sent to a public service.

## Next

- **Section-level AI.** Extend drafts from a summary to responsibilities, behaviour, side effects, error behaviour
  and usage, per symbol and per module. They would use the same inputs: facts marked by origin, the symbol's own
  source, and existing docs. They would keep per-sentence provenance to the facts they came from.
- **Incremental regeneration.** Regenerate only the pages whose inputs changed, using the node hashes and the
  impact map.

## Eventually

- HTML output.
- More languages and frameworks: SQL (stored procedures, migrations), React/Angular, Java/Spring.
