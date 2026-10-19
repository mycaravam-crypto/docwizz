# Signierte DOCX-/PDF-Publikation

DocWizz erzeugt **Entwürfe**. Die getrennte Operation `docwizz product publish` erfordert zwei
unabhängige, kryptografisch überprüfte Freigaben und eine unveränderte Git-Baseline.
Sie unterstützt die Lenkung dokumentierter Information nach ISO 9001 und ISO/IEC 27001
(7.5.2 / 7.5.3), aber bestätigt keine Zertifizierung oder organisationsweite Konformität.

## Vorbedingungen

Ein aus `docwizz product --control ... --context ...` erzeugter Markdown-Entwurf und sein
`.evidence.json`-Bericht müssen vorhanden sein. `product check` muss erfolgreich sein;
kein Pflichtkapitel darf unbelegt sein. Das Control-YAML enthält mindestens einen belegten
Review- und einen Freigabeeintrag. Dies ist **nicht** die kryptografische Freigabe.

Die getrennte Trust-Policy (geschützter Branch) speichert nur öffentliche Schlüssel:

```json
{"schema":1,"keys":[
  {"id":"qm-2026","role":"quality","public_key":".github/release-keys/qm.pem"},
  {"id":"isms-2026","role":"security","public_key":".github/release-keys/isms.pem"}
]}
```

Zwei verschiedene Schlüsselinaber signieren *dieselben Payload-Bytes* mittels
`openssl dgst -sha256 -sign private.pem -out signature.bin payload.json`.
**Private Schlüssel niemals in Git oder CI-Artefakten speichern.** Signierte Freigabe:

```json
{
  "schema":1,
  "payload_base64":"<Base64 der JSON-Payload>",
  "signatures":[
    {"role":"quality","key_id":"qm-2026","signature_base64":"<Base64-Signatur>"},
    {"role":"security","key_id":"isms-2026","signature_base64":"<Base64-Signatur>"}
  ]
}
```

Die originale UTF-8-JSON-Payload (die Signatur bindet ihre **exakten Bytes**) enthält:

```json
{
  "status":"approved",
  "document_id":"SWA-001",
  "version":"1.0",
  "draft_sha256":"<SHA-256 der Markdown-Datei>",
  "evidence_sha256":"<SHA-256 des evidence.json>",
  "control_sha256":"<SHA-256 des control.yaml>",
  "source_commit":"<40-stellige Git-Commit-SHA>"
}
```

## Aufruf

```bash
docwizz product check . templates/vmodell-xt/sw-architecture.yaml --control control.yaml --context architecture.yaml
docwizz product publish . templates/vmodell-xt/sw-architecture.yaml docs/published/SWA-001-1.0 \
  --context architecture.yaml --control control.yaml \
  --approval signed-approval.json --trust .github/document-release-policy.json
```

Ergebnis: `<ID>-<VERSION>.docx`, `<ID>-<VERSION>.pdf` und
`<ID>-<VERSION>.publication.json` mit Input-/Output-Hashes. Bereits bestehende
Zielverzeichnisse werden nicht überschrieben. Das aus dem Projekt stammende verlinkte Inhaltsverzeichnis bleibt im DOCX/PDF erhalten,
während die DOCX-Verarbeitung einen Seitenumbruch nach dem Deckblatt und dynamische Seitenzahlen (PAGE/NUMPAGES) ergänzt. LibreOffice erstellt das PDF; beide Renderer und Git müssen installiert sein.
Das PDF wird auf Dateisignatur geprüft, nicht auf PDF/A oder Barrierefreiheit.

## Workflow und tatsächliche Schutzgrenzen

- `.github/workflows/test.yml`: echte DOCX-/PDF-Erzeugung mit synthetischen, flüchtigen
  Schlüsseln sowie Negativtests für Manipulation, Doppelveröffentlichung und falsche Signatur.
- `.github/workflows/document-publication.yml`: manueller Ablauf nur auf `main`,
  nur in einem **privaten** Repository und nur über `document-release` mit Required Reviewers.
  Die Umgebung benötigt das geschützte Secret `DOCWIZZ_RELEASE_AUDIT_TOKEN` mit ausschließlich
  `Administration: Read` für die Abfrage der Environment-Schutzregeln; das normale `GITHUB_TOKEN`
  verfügt nicht über diese Berechtigung. Ohne diesen Audit-Token oder ohne Required Reviewers
  schlägt die Veröffentlichung bewusst fehl. Ein Administrator muss diese Umgebung einrichten;
  ohne nachweisbare Schutzregeln
  wird die Veröffentlichung verweigert.
- Trust-Policy und zugehörige Schlüssel dürfen nicht durch Antragsteller ohne Review
  ausgetauscht werden (Branch Protection, CODEOWNERS, getrennte Rollen).
- GitHub-Artefakte sind kein Ersatz für ein revisionssicheres DMS.

Nicht enthalten: Hardware-Schlüsselschutz, Widerrufsdienst, qualifizierte eIDAS-Signatur,
PDF/A-Validierung, DMS-Aufbewahrung, PDF-Layoutfreigabe oder ISO-Zertifizierung.
