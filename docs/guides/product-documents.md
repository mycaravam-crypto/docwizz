# Product documents

[Documentation home](../README.md) · [CLI reference](../cli.md)

`docwizz product` drafts a work product, such as a software architecture or an IT operations concept, from a YAML
template, the code model and statements you source yourself. The process has three separate states:

1. **Draft:** `docwizz product` writes Markdown and an evidence report. Sections without evidence say `OFFEN` (open).
2. **Checked:** `docwizz product check` verifies the draft's references and freshness.
3. **Published:** `docwizz product publish` renders signed DOCX and PDF after two independent approvals
   ([signed publication](../reference/publication.md)).

A draft, a declared review and a signed release are different things, and none of them is a conformity claim.

Templates: [V-Modell XT examples](vmodell-xt-templates.md) (software architecture, software specification,
system-element test specification) and the [IT operations concept](it-operations.md).

## Drafts

```bash
docwizz product . templates/vmodell-xt/sw-architecture.yaml [out.md] [--context project.yaml] [--control document-control.yaml] [--ai]
```

Renders the template's sections in order. The code model fills two of them: `structure` (top-level building blocks)
and `interfaces` (code dependencies), each limited to 40 entries, so neither proves completeness. Everything else comes
from `--context`. Output defaults to `docs/products/<product_id>.md` next to `<product_id>.evidence.json`. Paths
resolve from the current directory, then from `<dir>`. Unchanged output isn't rewritten, and a file docwizz didn't
write is never replaced.

**`--context project.yaml`** adds statements, each with a source document:

```yaml
schema: 1
statements:
  - section: scope
    text: The software unit processes order data within the documented system boundary.
    source: docs/project-charter.md
```

The `section` must be a template section that accepts project evidence, `text` a single line, and `source` a
repository-relative Markdown file under `docs/`. A statement that fits no section exits 1; nothing is silently
dropped. Statements are maintained by people, not approved by them.

**`--ai`** has the self-hosted model ([AI](../configuration.md#ai)) word each section that has evidence. Each piece of
evidence gets an id (`E1`, `E2`, …) and every sentence must cite ids from its own section. A sentence that cites
nothing, cites wrongly, or claims approval, review, completeness or conformity is dropped, and model text can't become
links or HTML. Sentences are marked 🤖, sections without evidence are never sent, and project statements are sent as
data only. Results are cached in `docs/.docwizz/product-ai-cache.json`; if nothing survives, the output equals the
draft without `--ai`.

**`<product_id>.evidence.json`** lists per section its status (`evidenced`, `open`, `empty`), every piece of evidence
with id, kind, origin (`detected` or `human`) and source, and the 🤖 sentences with their citations. It is an
inventory, not a review.

## Checks

```bash
docwizz product check . templates/vmodell-xt/sw-architecture.yaml --context project.yaml [--control document-control.yaml] [--format json]
```

Exits 1 on an error; warnings never fail.

| Rule | Severity | Finds |
|---|---|---|
| PROD-001 | warning | a required section without evidence (`OFFEN`) |
| PROD-002 | error | a project statement whose source file doesn't exist |
| PROD-003 | error | a product in `dependencies` without a draft next to this one |
| PROD-004 | error | the draft or its evidence report missing, not docwizz's, or stale |
| PROD-005 | warning | a project statement that reads like an instruction to a model |
| PROD-006 | error | a 🤖 sentence citing an id that isn't evidence of its section |
| DOC-001 | error | with `--control`: the control metadata is missing from the evidence report or changed since the draft (SHA-256) |
| DOC-002 | error | with `--control`: a linked review or approval document doesn't exist |
| DOC-003 | warning | with `--control`: no external approval is declared |

The check never judges content: whether a statement is true or a product can be approved stays a human decision.

## Document control

`--control document-control.yaml` adds a document frame to the draft: a cover page with identity, author, role, date,
classification and status; the revision history; external review and approval references; distribution and retention
rules; and a clickable table of contents with an anchor per template section. Copy
[document-control.example.yaml](../../templates/vmodell-xt/document-control.example.yaml) into the project and replace
the example values.

| Field | Meaning |
|---|---|
| `document_id`, `title`, `version`, `created` | stable identity and revision |
| `author`, `owner` | who wrote the document and who is responsible for it |
| `classification`, `distribution` | declared classification and distribution rule |
| `retention` | retention and disposal rule |
| `changes` | at least one change record for the current revision (`version`, `date`, `author`, `description`) |
| `approvals` | optional declared external evidence: `kind: review\|approval`, `role`, `person`, `date`, `evidence: docs/*.md` |

`schema: 1` and `schema: "1"` are both accepted. Values are single lines of at most 500 characters, because they are
rendered in tables: detailed rules and review results belong in versioned, access-controlled documents that you link
as `evidence`. Dates use `yyyy-MM-dd`. Unknown keys and unsafe evidence paths are rejected. Drafts and checks accept
template, context and control files from anywhere, such as a shared docwizz checkout; `product publish` accepts them
only inside the repository, so keep versioned copies in the project.

The output stays marked as a **draft** even when an approval is declared: docwizz can't verify that an approval is
genuine or authorized, and a hash of an editable file is no independent integrity proof. The table of contents is a
list of links, not a paginated Word field; page numbers come with [publication](../reference/publication.md).

The structure supports controlled documented information as in ISO 9001:2015 and ISO/IEC 27001:2022 (clauses 7.5.2
and 7.5.3, both including Amd 1:2024). It is no substitute for a document management system, qualified signatures,
access control or certification. An auditable process also needs: documented review and authorized approval with
traceable identity and decision; access control, classification and protection of confidential content; tamper-proof
retention, recovery and disposal; control of external documents and withdrawal of obsolete versions; and evidence that
the responsible people follow these rules, including internal audits.
