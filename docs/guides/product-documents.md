# Product documents and controlled publication

[Documentation home](../README.md) · [CLI reference](../cli.md)

## Draft from source and evidence

```bash
docwizz product . templates/vmodell-xt/sw-architecture.yaml --context project.yaml
docwizz product check . templates/vmodell-xt/sw-architecture.yaml --context project.yaml
```

Templates are **illustrative** and require project-specific tailoring. Unsubstantiated sections remain `OFFEN`; optional AI may phrase **cited existing evidence** but cannot create approvals or compliance.

- [Product template guide](../vmodell-xt-templates.md)
- [IT operations concept](../IT_OPERATIONS.md)
- [Explicit project evidence and traceability](../project-evidence.md)

## Govern the draft and publish it separately

1. Attach required cover, metadata and revision history with `--control`: [document control](../document-control.md).
2. Validate the draft and all cited sources with `docwizz product check`.
3. Apply the independent, signed two-party publication gate: [controlled DOCX/PDF publication](../document-publication.md).

A Markdown draft, an external approval reference and a signed release are different states. Generating DOCX/PDF is **not** a general ISO-conformity claim.
