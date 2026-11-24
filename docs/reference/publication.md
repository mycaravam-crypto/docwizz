# Signed DOCX/PDF publication

[Documentation home](../README.md) · [Product documents](../guides/product-documents.md)

docwizz writes **drafts**. The separate `docwizz product publish` renders a draft as DOCX and PDF only with two
independent, cryptographically verified approvals and an unchanged Git baseline. It supports the control of documented
information under ISO 9001 and ISO/IEC 27001 (7.5.2, 7.5.3), but confirms no certification or organization-wide
conformity.

## Preconditions

- A Markdown draft written by `docwizz product --control … --context …` and its `.evidence.json` report.
- `product check` passes, with no required section left without evidence.
- The control YAML declares at least one review and one approval with evidence. This is **not** the cryptographic
  approval.
- Git, Pandoc (for the DOCX) and LibreOffice (`libreoffice`, for the PDF) are installed.

A separate trust policy, kept on a protected branch, holds only public keys:

```json
{"schema":1,"keys":[
  {"id":"qm-2026","role":"quality","public_key":".github/release-keys/qm.pem"},
  {"id":"isms-2026","role":"security","public_key":".github/release-keys/isms.pem"}
]}
```

Two different key holders sign *the same payload bytes*, for example with
`openssl dgst -sha256 -sign private.pem -out signature.bin payload.json`. **Never store private keys in Git or CI
artifacts.** The payload is UTF-8 JSON, and the signatures bind its exact bytes:

```json
{
  "status":"approved",
  "document_id":"SWA-001",
  "version":"1.0",
  "draft_sha256":"<SHA-256 of the Markdown draft>",
  "evidence_sha256":"<SHA-256 of the evidence.json>",
  "control_sha256":"<SHA-256 of the control YAML>",
  "source_commit":"<40-character Git commit SHA>"
}
```

The signed approval wraps it:

```json
{
  "schema":1,
  "payload_base64":"<Base64 of the payload>",
  "signatures":[
    {"role":"quality","key_id":"qm-2026","signature_base64":"<Base64 signature>"},
    {"role":"security","key_id":"isms-2026","signature_base64":"<Base64 signature>"}
  ]
}
```

## Running it

```bash
docwizz product check . templates/vmodell-xt/sw-architecture.yaml --control control.yaml --context architecture.yaml
docwizz product publish . templates/vmodell-xt/sw-architecture.yaml docs/published/SWA-001-1.0 \
  --context architecture.yaml --control control.yaml \
  --approval signed-approval.json --trust .github/document-release-policy.json
```

The result is `<ID>-<VERSION>.docx`, `<ID>-<VERSION>.pdf` and `<ID>-<VERSION>.publication.json` with the input and
output hashes. An existing output directory is never overwritten. The draft's linked table of contents is kept; the
DOCX gets a page break after the cover and page numbers (PAGE/NUMPAGES). The PDF is checked for its file signature, not
for PDF/A or accessibility.

By default `publish` takes the generated draft `docs/products/<product_id>.md` and the evidence report next to it. For a
draft written elsewhere, select it with `--draft docs/custom/my-product.md`; the signed payload must then contain the
hashes of **that** file and its `.evidence.json`. `--draft` doesn't change where the DOCX and PDF go.

## Safety rules

- Every input (template, context, control, approval, trust policy, draft and evidence report) and the output directory
  must be inside the repository. Paths are checked after normalization, and symbolic links in a path are refused.
  Plain `product` and `product check` may still use shared files from elsewhere; they approve nothing.
- The version used in the file names must be 1–80 characters of letters, digits, `.`, `_` and `-`, starting with a
  letter or digit.
- Bad or tampered signatures, a missing draft or any missing precondition is a normal CLI error with exit code 1,
  never a publication.

## CI and its real protection

- `.github/workflows/test.yml` renders real DOCX and PDF with throwaway keys, and tests tampering, double publication
  and wrong signatures.
- `.github/workflows/document-publication.yml` is a manual workflow that runs only on `main`, only in a **private**
  repository, and only through the `document-release` environment with required reviewers. That environment needs the
  protected secret `DOCWIZZ_RELEASE_AUDIT_TOKEN` with only `Administration: Read`, to read the environment's protection
  rules (the normal `GITHUB_TOKEN` can't). Without the token or without required reviewers, publication fails on
  purpose. An administrator has to set the environment up.
- The trust policy and its keys must not be replaceable by the person requesting publication without review (branch
  protection, CODEOWNERS, separate roles).
- GitHub artifacts are no substitute for a tamper-proof document management system.

Not included: hardware key protection, revocation, qualified eIDAS signatures, PDF/A validation, archival retention,
PDF layout approval or ISO certification. Ownership of sources, roles and keys stays an organizational task.
