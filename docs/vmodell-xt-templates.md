# Praxisnahe V-Modell-XT-Produktvorlagen

DocWizz liefert drei **illustrative, projektbezogen anzupassende** Produktvorlagen im kompatiblen `schema: 1`. Sie sind keine amtlichen V-Modell-XT-Vorlagen und keine Konformitäts- oder Freigabeaussage.

| Produkt | YAML-Vorlage | Ausfüll- und Reviewhilfe | Abschnitte |
|---|---|---|---:|
| SW-Architektur | [sw-architecture.yaml](../templates/vmodell-xt/sw-architecture.yaml) | [SW-Architektur](vmodell-xt/sw-architecture-guide.md) | 17 |
| SW-Spezifikation | [sw-specification.yaml](../templates/vmodell-xt/sw-specification.yaml) | [SW-Spezifikation](vmodell-xt/sw-specification-guide.md) | 17 |
| Systemelement-Testspezifikation | [system-element-test-specification.yaml](../templates/vmodell-xt/system-element-test-specification.yaml) | [Testspezifikation](vmodell-xt/system-element-test-specification-guide.md) | 15 |

## Verwendung

Im zu dokumentierenden Software-Repository ausführen. Kopiere die benötigten Vorlagen zunächst aus dem DocWizz-Checkout in ein versioniertes Verzeichnis innerhalb des zu dokumentierenden Projekts (keine externen absoluten Template-Pfade); `docwizz` muss installiert beziehungsweise als CLI-Alias verfügbar sein. Vorher `DOCWIZZ_ROOT` auf den **absoluten Pfad** des lokalen DocWizz-Checkouts setzen (beispielsweise `export DOCWIZZ_ROOT="$HOME/src/docwizz"`, wenn DocWizz dort ausgecheckt ist). Die `--context`- und Evidenz-Dateien sind projektspezifisch anzulegen.

```bash
: "${DOCWIZZ_ROOT:?DOCWIZZ_ROOT auf den DocWizz-Checkout setzen}"
mkdir -p templates/vmodell-xt
cp "$DOCWIZZ_ROOT"/templates/vmodell-xt/*.yaml templates/vmodell-xt/
docwizz setup .

docwizz product . "templates/vmodell-xt/sw-specification.yaml" --context sw-specification-context.yaml
docwizz product . "templates/vmodell-xt/sw-architecture.yaml" --context sw-architecture-context.yaml
docwizz product . "templates/vmodell-xt/system-element-test-specification.yaml" --context system-element-test-specification-context.yaml

docwizz product check . "templates/vmodell-xt/sw-specification.yaml" --context sw-specification-context.yaml
docwizz product check . "templates/vmodell-xt/sw-architecture.yaml" --context sw-architecture-context.yaml
docwizz product check . "templates/vmodell-xt/system-element-test-specification.yaml" --context system-element-test-specification-context.yaml

docwizz trace . project-evidence.yaml
```

Projektkontexte verwenden `schema: 1`, `statements` mit exakt passenden Abschnitts-IDs und belegten relativen `docs/*.md`-Quellen. Für jede Produktvorlage ist ein eigener Kontext sinnvoll. Beispiel für `sw-architecture-context.yaml`:

```yaml
schema: 1
statements:
  - section: scope
    text: Die Softwareeinheit verarbeitet Auftragsdaten innerhalb der dokumentierten Systemgrenze.
    source: docs/project-charter.md
  - section: decisions
    text: Die Entscheidung zur Trennung von Anwendungslogik und Infrastruktur ist im ADR dokumentiert.
    source: docs/adr/0001-architecture.md
```

Die referenzierten Dateien müssen im Software-Repository existieren; der Inhalt oben ist nur beispielhaft und muss durch projektspezifische Nachweise ersetzt werden.

## Grenzen und Review

- Aus Code werden derzeit **nur** die Abschnitts-IDs `structure` und `interfaces` automatisch befüllt, auf höchstens 40 erkannte Einträge je Abschnitt begrenzt. Die übrigen Kapitel benötigen verifizierbare Projektangaben.
- Die YAML-Kommentare erläutern erwartete Belege, erscheinen aber **nicht** in den generierten Produktseiten. Deshalb liegen separate Ausfüllhilfen neben den Vorlagen.
- `required: true` ist eine Arbeitserwartung der Beispielvorlage, keine für jedes Projekt verbindliche Produktpflicht. Vor Nutzung die gültige V-Modell-XT-Variante, Version, Rollen, Produktabhängigkeiten und Tailoring-Entscheidungen prüfen.
- Die Vorlage verwendet `xt_version: "2.4"` als bestehende Metadatenangabe. Öffentliche Strukturreferenzen aus **V-Modell XT Bund 2.0** müssen vor einer verbindlichen Anwendung auf Abweichungen geprüft werden.
- `product check` prüft Dateiverweise und Aktualität der Entwürfe, nicht die fachliche Richtigkeit, Vollständigkeit oder autorisierte Freigabe. `trace` wertet ausschließlich deklarierte Anforderung–Architektur–Test-Verknüpfungen aus; es führt keine Tests durch.
- Bewertbare Qualitätsziele können sich an ISO/IEC 25010 orientieren. Sicherheitsanforderungen (Vertraulichkeit, Integrität, Verfügbarkeit) benötigen projektspezifische Nachweise; ISO/IEC 27001 ersetzt keine Produktprüfung.
- Die Testspezifikation hat die beispielhafte direkte Abhängigkeit `sw-specification`; andere Produktbeziehungen müssen im Projekt-Tailoring explizit entschieden werden.

Weitere Details: [Product drafts](../CLI.md#product-drafts), [Project evidence](project-evidence.md), [Template contract](adr/0001-product-template-contract.md).

## Fachliche Referenzen

- V-Modell XT Bund 2.0 – [SW-Architektur](https://download.gsb.bund.de/BundesCIO/V-Modell_XT_Bund/V-Modell%20XT%20Bund-2.0-HTML/13992f89ccc6aa1.html)
- V-Modell XT Bund 2.0 – [SW-Spezifikation](https://download.gsb.bund.de/BundesCIO/V-Modell_XT_Bund/V-Modell%20XT%20Bund-2.0-HTML/10242fa1910292f.html)
- V-Modell XT Bund 2.0 – [Prüfspezifikation Systemelement](https://download.gsb.bund.de/BundesCIO/V-Modell_XT_Bund/V-Modell%20XT%20Bund-2.0-HTML/af30f82600e73e.html)
- [arc42 – Architektur-Praxisbeispiele](https://examples.arc42.org/)
