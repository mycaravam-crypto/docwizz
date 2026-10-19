# DOCX/PDF-Publikation mit verbindlichem Freigabe-Gate

Diese Funktion ergänzt den kontrollierten Markdown-Entwurf aus [Dokumentenlenkung](document-control.md). Sie implementiert **keine** automatische ISO-9001-/27001-Zertifizierung und ersetzt kein revisionssicheres DMS.

## Entwürfe

Voraussetzungen: Python 3.12, `python-docx==1.2.0`, LibreOffice Writer. Das Produkt muss zuerst mit `docwizz product ... --control control.yaml` erstellt werden. Danach:

```bash
python tools/publish_product.py --mode draft \
  --source docs/products/sw-architecture.md \
  --control document-control.yaml \
  --evidence docs/products/sw-architecture.evidence.json \
  --output dist
```

Entwurfsdateien werden sichtbar als **NICHT FREIGEGEBEN** gekennzeichnet. DOCX hat eine Titelseite, ein Word-TOC-Feld, Abschnittsüberschriften, Tabellen und Seitenzahlen; PDF wird mit LibreOffice erzeugt. Beim Öffnen in Word ggf. das Inhaltsverzeichnis aktualisieren.

## Freigabepfad – fail closed

1. Review und Freigabe im festgelegten QM/ISMS-Prozess durch **verschiedene, berechtigte Personen** durchführen und im DMS nachweisen.
2. Ein eigenständiger Freigabedienst erstellt aus den unveränderten drei Quelldateien ein signiertes Attest. Der Signaturschlüssel liegt **nicht** im Repository oder im PR-Runner.
3. GitHub Environment **`document-release`** mit *Required Reviewers*, *prevent self-review* und ausschließlich Branch `main` konfigurieren. Secret `DOCWIZZ_APPROVAL_KEY` mit mindestens 32 Zeichen ausschließlich für diese Environment hinterlegen. Ohne diese administrative Einrichtung ist keine Freigabe möglich.
4. In Actions den Workflow **controlled-document-publication → Run workflow** auf `main` mit den Pfaden `source`, `control`, `template`, optional `context`, `attestation` auslösen. Zuerst muss `docwizz product check` fehlerfrei sein. Nur danach werden DOCX/PDF erzeugt und als GitHub-Artefakte hochgeladen.

Das Attest ist JSON mit genau `payload` und `signature`:

```json
{
  "payload": {
    "schema": 1,
    "document_id": "SWA-001",
    "source_sha256": "<sha256 of markdown bytes>",
    "control_sha256": "<sha256 of YAML bytes>",
    "evidence_sha256": "<sha256 of evidence JSON bytes>",
    "reviewer": "person-a",
    "approver": "person-b"
  },
  "signature": "<HMAC-SHA256 hex of compact sorted-key UTF-8 JSON payload>"
}
```

Zur Implementierung der Signatur muss die genehmigende Instanz `HMAC_SHA256(DOCWIZZ_APPROVAL_KEY, json.dumps(payload, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8"))` verwenden. Diese Signatur muss **außerhalb** des GitHub-Laufs nach erfolgter autorisierter Freigabe erstellt werden. Ein manuell erzeugtes Attest ohne Kontrollprozess ist nicht ausreichend.

**Gate-Regeln:** Der Exporter lehnt fehlende Eingaben, ungültige Signaturen, unterschiedliche SHA-256-Hashes, gleiche Prüfer-/Freigeberidentität, offene Pflichtabschnitte, fehlenden vorgeschalteten Product-Check und fehlendes Secret ab. Bereits ein verändertes Dokument macht das Attest ungültig.

**Wichtige Grenzen:** Der HMAC beweist nur Kenntnis des Schlüssels und die Unverändertheit der signierten Bytes; er attestiert nicht eigenständig die Identität, Berechtigung oder Qualität des Freigeberprozesses. GitHub-Actions-Artefakte werden nach 30 Tagen gelöscht; für die ISO-relevante Aufbewahrung ist eine zusätzliche unveränderliche DMS-Ablage mit definierten Rechten, Retention, Backup und Audittrail erforderlich. Die PDF-Ausgabe ist keine qualifizierte elektronische Signatur und kein PDF/A-Nachweis.

## CI

Der separate Workflow führt die Gate-Unit-Tests auf jedem einschlägigen PR und Push aus. Er verlangt **keine Secrets** für PR-Tests und kann keine Freigaben aus einem PR erstellen. Der normale Software-Release-Workflow wird nicht verändert.
