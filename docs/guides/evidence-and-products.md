# Evidence and product drafts

[Documentation home](../README.md) · [CLI reference](../cli.md)

## Project evidence

These commands work on requirement and project evidence kept as YAML, CSV or Markdown, in the format described in
[docs/project-evidence.md](../project-evidence.md). None of them make network or AI calls (except `product --ai`),
overwrite source files, or claim conformity.

### Traceability report

```bash
docwizz trace . project-evidence.yaml [--format json]
```

Reports declared requirement → architecture → test links and the missing ones, as Markdown on stdout or JSON. CSV and
Markdown inputs contain records only, without links. Missing links are gaps, not failures; an invalid or missing
catalog exits 1. With a `source_hash`, each source is reported as current, stale, missing or unverified. It checks
declared links only, not test success.

### Evidence snapshot comparison

```bash
docwizz evidence-diff . before.yaml after.yaml [--format json]
```

Compares two evidence snapshots: changed fields, direct links, tests reachable along declared relations, added and
removed links. Output is deterministically ordered. It doesn't inspect file contents or decide whether evidence is
current.

### Product drafts

Optional ISO-oriented document control: [cover, clickable contents and document history](../document-control.md), activated with `--control document-control.yaml` for `product` and `product check`.

```bash
docwizz product . templates/vmodell-xt/sw-architecture.yaml [out.md] [--context project.yaml] [--control document-control.yaml] [--ai]
docwizz product check . templates/vmodell-xt/sw-architecture.yaml --context project.yaml [--control document-control.yaml] [--format json]
```

Renders a work product from a template, filled from the code model; anything without evidence stays `OFFEN`. Output
defaults to `docs/products/<product_id>.md`. Paths resolve from the current directory, then from `<dir>`. Unchanged
output isn't rewritten, and hand-maintained output is never replaced. The V-Modell XT templates are illustrative, not
official templates or a conformity claim.

For an IT operations concept, use `templates/it-operations/betriebskonzept.yaml` (generic
`document_type: it-operations`, without `xt_variant`/`xt_version`/`tailoring`). This is a
source-backed **draft**, not evidence of operational readiness. Recognised deployment/CI descriptor files are inventoried by source path only; deployment procedures stay `OFFEN` until independently evidenced. See
[IT operating concept](../IT_OPERATIONS.md) for generated sections and explicit limits.


- **`--context project.yaml`** adds sourced project statements. Statements that don't fit a section exit 1; nothing is
  silently dropped.
- **`--ai`** has a self-hosted model (the `ai:` provider) word each section that has evidence. Each piece of evidence
  gets an id (`E1`, `E2`, …) and every sentence must cite ids from its own section. A sentence that cites nothing, cites
  wrongly, or claims approval, review, completeness or conformity is dropped, and model text can't become links or
  HTML. Sentences are marked 🤖; sections without evidence are never sent; project statements are sent as data only.
  Results are cached in `docs/.docwizz/product-ai-cache.json`. If nothing could be worded, the output equals the draft
  without `--ai`.
- **`<product_id>.evidence.json`** lists per section its status (`evidenced`, `open`, `empty`), every piece of evidence
  with id, kind, origin (`detected` or `human`) and source, and any 🤖 sentences with their citations. `required`
  summarizes which required sections are evidenced or open. It is an inventory, not a statement of review.

`product check` validates a draft's references and freshness before review (exit 1 on an error; warnings never fail):

| Rule | Severity | Finds |
|---|---|---|
| PROD-001 | warning | a required section without evidence (`OFFEN`) |
| PROD-002 | error | a project statement whose source file doesn't exist |
| PROD-003 | error | a product in `dependencies` without a draft next to this one |
| PROD-004 | error | the draft or its evidence report missing, not docwizz's, or stale |
| PROD-005 | warning | a project statement that reads like an instruction to a model |
| PROD-006 | error | a 🤖 sentence citing an id that isn't evidence of its section |

It never judges content: whether a statement is true or a product can be approved stays a human decision.
