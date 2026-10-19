# Gelenkte Produktdokumente: Deckblatt, Inhaltsverzeichnis und Dokumentenlenkung

DocWizz kann optional einen wiederverwendbaren Dokumentenrahmen für jedes Produkt erzeugen. Das ist **kein** Ersatz für ein Dokumentenmanagementsystem (DMS), eine qualifizierte Signatur, Zugriffskontrolle oder eine ISO-Zertifizierung.

## Verwendung

Kopiere [document-control.example.yaml](../templates/vmodell-xt/document-control.example.yaml) in das Softwareprojekt und ersetze die Beispielangaben durch die tatsächliche Dokumenten-ID, Revision, Verantwortlichkeiten, Klassifizierung und organisatorischen Regeln.

Das Dateiformat bleibt separat vom bestehenden Produkttemplate-\`schema: 1\`. Die zusätzliche CLI-Option ist optional; frühere Produktentwürfe bleiben ohne sie unverändert.

\`\`\`bash
docwizz product . /path/to/docwizz/templates/vmodell-xt/sw-architecture.yaml --control document-control.yaml --context project-architecture.yaml
docwizz product check . /path/to/docwizz/templates/vmodell-xt/sw-architecture.yaml --control document-control.yaml --context project-architecture.yaml
\`\`\`

\`product\` schreibt weiter \`docs/products/sw-architecture.md\` und \`sw-architecture.evidence.json\`. Im gelenkten Modus enthält der Entwurf ein Deckblatt mit Identität, Autor, Rolle, Datum, Schutzklassifizierung und Status, die Revisionshistorie, externe Prüf-/Freigabereferenzen, Verteil- und Aufbewahrungsvorgaben sowie ein anklickbares Inhaltsverzeichnis. Ein expliziter Kapitelanker wird je Produkttemplate-Abschnitt erzeugt.

Die \`--control\`-Datei verwendet folgendes **Schema 1**:

| Feld | Bedeutung |
|---|---|
| \`document_id\`, \`title\`, \`version\`, \`created\` | stabile Identifikation und Versionsstand |
| \`author\`, \`owner\` | Erstellung und Dokumentverantwortung |
| \`classification\`, \`distribution\` | deklarierte Klassifizierung und Verteilregel |
| \`retention\` | organisatorische Aufbewahrungs- und Aussonderungsregel |
| \`changes\` | mindestens ein Änderungsdatensatz zur aktuellen Revision (\`version\`, \`date\`, \`author\`, \`description\`) |
| \`approvals\` | optionale deklarierte externe Nachweise mit \`kind: review|approval\`, \`role\`, \`person\`, \`date\`, \`evidence: docs/*.md\` |

Die Angaben `schema: 1` und `schema: "1"` werden gleichermaßen akzeptiert. Für
Deckblatt und Revisionstabellen gelten kurze einzeilige Metadatenfelder (maximal
500 Zeichen je Wert): Zeilenumbrüche oder umfangreiche Freitexte würden das
Markdown-Tabellenlayout beschädigen. Ausführliche Verteilungs-/Aufbewahrungsregeln
und Prüfergebnisse gehören in versionierte, zugriffsgeschützte Belegdokumente
statt in Metadatenwerte. DocWizz authentifiziert diese Belege nicht.
Datumsfelder müssen \`yyyy-MM-dd\` verwenden. Unbekannte Schlüssel und unsichere Nachweispfade werden abgelehnt. \`product check\` prüft bei aktivierter Lenkung zusätzlich:

- **DOC-001 (error):** Kontrollmetadaten fehlen im Nachweisbericht oder haben sich seit der Generierung geändert (SHA-256).
- **DOC-002 (error):** verlinkte Prüf-/Freigabedatei fehlt.
- **DOC-003 (warning):** kein externer Freigabenachweis deklariert.

Jede generierte Ausgabe bleibt ausdrücklich als **Entwurf** gekennzeichnet, auch wenn ein Nachweis als \`approval\` erfasst ist. Die Echtheit und Berechtigung einer Genehmigung kann DocWizz nicht feststellen. Ein Hash derselben bearbeitbaren Datei ist kein unabhängiger Integritätsnachweis.

## ISO-Bezug und notwendige organisatorische Kontrollen

Die Ausgabestruktur **unterstützt** ISO 9001:2015 (inklusive Amd 1:2024) sowie ISO/IEC 27001:2022 (inklusive Amd 1:2024), insbesondere die dokumentierte Information gemäß **7.5.2** und **7.5.3**. Eine pauschale Konformitätsaussage wird nicht getroffen. Für einen auditierbaren Prozess sind darüber hinaus erforderlich:

1. dokumentierter Review und autorisierte Freigabe, einschließlich nachvollziehbarer Identität und Entscheidung;
2. wirksame Zugriffskontrollen, Klassifizierungsregeln, Verteilung und Schutz vertraulicher Inhalte;
3. unveränderbare beziehungsweise revisionssichere Aufbewahrung, Wiederherstellbarkeit und geregelte Aussonderung;
4. Kontrolle externer Dokumente sowie Rückzug veralteter Versionen;
5. nachweisbare Durchführung dieser Regeln durch Verantwortliche und im Rahmen interner Audits.

Das Markdown-Verzeichnis enthält anklickbare Kapitel, **kein** Word-Feld für ein paginiertes Inhaltsverzeichnis. DOCX-/PDF-Veröffentlichung mit Seitenzahlen, Seitengestaltung und signierbaren Freigaben ist eine eigenständige Folgestufe.

Quellen: [ISO 9001](https://www.dinmedia.de/de/norm/din-en-iso-9001/235671251), [ISO/IEC 27001](https://committee.iso.org/cms/live/live/en/sites/isoorg/contents/data/standard/08/84/88435.html?browse=tc), [DocWizz Product drafts](cli.md#product-drafts).
