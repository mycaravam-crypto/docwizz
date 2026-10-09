# ADR-0001: Eigenstaendiger Produkt-Template-Vertrag

Status: vorgeschlagen (P0, Issue #93)

## Kontext
`Profiles.cs` beschreibt die Abdeckung von Code-Dokumentation, nicht den Inhalt von
V-Modell-XT-Produkten. Die vorhandenen Generatoren erzeugen Code- und Architekturansichten,
waehrend projektspezifischer Zweck, Entscheidungen und Freigaben nicht aus Code ableitbar sind.

## Entscheidung
Ein kleines YAML-Schema v1 definiert Produktkennung, XT-Variante und -Version,
projektbezogenes Tailoring, geordnete Kapitel (ID, Titel, Pflichtfeld, zugelassene
Quellenarten) und unmittelbare Produktabhaengigkeiten. Ein unabhaengiger Validator
verwirft unbekannte Felder und mehrdeutige oder unvollstaendige Vorlagen vor jeder Generierung.

`code` und `project` beschreiben **zulaessige Quellenarten**, noch keine angebundenen
Quellen. Die Datei `templates/vmodell-xt/sw-architecture.yaml` ist ein
illustratives Beispiel, **keine offizielle V-Modell-XT-Vorlage**.

## Folgen und bewusste Nichtziele
- Keine Aenderung an `--profile`, `Generator`, `AiProse` oder existierenden CLI-Aufrufen.
- Keine neue KI-Abstraktion, Datenbank, RAG, DOCX- oder Workflow-Engine in P0.
- P1 darf nur belegte Aussagen erzeugen; fehlende projektspezifische Angaben bleiben OFFEN.
- Versions- und Variantenbezug ist Metadatum, kein Konformitaetsnachweis.
- Eine Uebernahme offizieller Vorlagentexte erfordert separat geklaerte Nutzungsrechte.
