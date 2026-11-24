# V-Modell XT templates

[Documentation home](../README.md) · [Product documents](product-documents.md)

docwizz ships three illustrative product templates for [`docwizz product`](product-documents.md), with the
V-Modell XT's German product names in italics. They are original outlines to tailor to your project: not official V-Modell XT templates, and no
statement of conformity or approval.

| Product | Template | Filling and review guide | Sections |
|---|---|---|---:|
| Software architecture (*SW-Architektur*) | [sw-architecture.yaml](../../templates/vmodell-xt/sw-architecture.yaml) | [guide](../reference/vmodell-xt/sw-architecture.md) | 17 |
| Software specification (*SW-Spezifikation*) | [sw-specification.yaml](../../templates/vmodell-xt/sw-specification.yaml) | [guide](../reference/vmodell-xt/sw-specification.md) | 17 |
| System-element test specification (*Prüfspezifikation Systemelement*) | [system-element-test-specification.yaml](../../templates/vmodell-xt/system-element-test-specification.yaml) | [guide](../reference/vmodell-xt/system-element-test-specification.md) | 15 |

The YAML comments explain the evidence each section expects but don't appear in the drafts; the filling guides carry
the same expectations with examples and review questions.

## Usage

Run in the repository you document. Copy the templates into it first, so they are versioned with the project (signed
[publication](../reference/publication.md) accepts only files inside the repository):

```bash
: "${DOCWIZZ_ROOT:?set DOCWIZZ_ROOT to the absolute path of your docwizz checkout}"
mkdir -p templates/vmodell-xt
cp "$DOCWIZZ_ROOT"/templates/vmodell-xt/*.yaml templates/vmodell-xt/
docwizz setup .

docwizz product . templates/vmodell-xt/sw-specification.yaml --context sw-specification-context.yaml
docwizz product . templates/vmodell-xt/sw-architecture.yaml --context sw-architecture-context.yaml
docwizz product . templates/vmodell-xt/system-element-test-specification.yaml --context system-element-test-specification-context.yaml

docwizz product check . templates/vmodell-xt/sw-architecture.yaml --context sw-architecture-context.yaml
docwizz trace . project-evidence.yaml
```

Write one context file per template, with statements for its section ids and sources that exist in the repository:

```yaml
schema: 1
statements:
  - section: scope
    text: The software unit processes order data within the documented system boundary.
    source: docs/project-charter.md
  - section: decisions
    text: The separation of application logic and infrastructure is recorded in an ADR.
    source: docs/architecture/decisions.md
```

## Limits

- Only the sections `structure` and `interfaces` are filled from code, with at most 40 entries each. Every other
  section needs verifiable project statements.
- `required: true` is the example template's working assumption, not an obligation for every project. Check the
  V-Modell XT variant, version, roles, product dependencies and tailoring decisions that apply to you.
- The templates declare `xt_version: "2.4"`. Check the public structure of V-Modell XT Bund 2.0 for differences before
  binding use.
- The test specification depends on `sw-specification` as an example; decide other product dependencies in your
  tailoring. For requirement → test links, use [project evidence](project-evidence.md) and `docwizz trace`, not a
  relationship assumed from a chapter title.
- `product check` verifies file references and freshness, not correctness, completeness or authorized approval.
  `trace` evaluates declared links only and runs no tests.
- Quality goals can follow ISO/IEC 25010. Security requirements (confidentiality, integrity, availability) need
  project evidence; ISO/IEC 27001 is no substitute for product verification.
- Check your rights to reuse official template wording before adding it.

The template format is defined in [the product template decision](../internals/decisions/product-template-contract.md).

## References

- V-Modell XT Bund 2.0: [SW-Architektur](https://download.gsb.bund.de/BundesCIO/V-Modell_XT_Bund/V-Modell%20XT%20Bund-2.0-HTML/13992f89ccc6aa1.html)
- V-Modell XT Bund 2.0: [SW-Spezifikation](https://download.gsb.bund.de/BundesCIO/V-Modell_XT_Bund/V-Modell%20XT%20Bund-2.0-HTML/10242fa1910292f.html)
- V-Modell XT Bund 2.0: [Prüfspezifikation Systemelement](https://download.gsb.bund.de/BundesCIO/V-Modell_XT_Bund/V-Modell%20XT%20Bund-2.0-HTML/af30f82600e73e.html)
- [arc42 architecture examples](https://examples.arc42.org/)
