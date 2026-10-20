# IT-Betriebskonzept: evidenzbasierter Entwurf (#261)

DocWizz erzeugt eine technische **Grundlage** für ein Betriebskonzept, kein freigegebenes Betriebshandbuch und keine ISO-Konformitätsaussage.

## Anwendung

Das Template liegt im DocWizz-Checkout:

~~~bash
docwizz product /pfad/zur/anwendung /pfad/zu/docwizz/templates/it-operations/betriebskonzept.yaml
docwizz product check /pfad/zur/anwendung /pfad/zu/docwizz/templates/it-operations/betriebskonzept.yaml --format json

# Explizit dokumentierte Projektinformationen und optionale lokale/interne KI:
docwizz product /pfad/zur/anwendung /pfad/zu/docwizz/templates/it-operations/betriebskonzept.yaml --context project.yaml --ai
~~~

Es entstehen `docs/products/it-betriebskonzept.md` und `it-betriebskonzept.evidence.json`.
Beide Artefakte unterliegen dem bestehenden Überschreibschutz. Der Entwurf ist ohne KI vollständig offline und reproduzierbar.

## Umfang des MVP

| Kapitel | Automatische Quelle | Grenze |
|---|---|---|
| Systemstruktur | bestehende CodeModel-Knoten und Fundstellen | kein fachliches Soll-Modell |
| Laufzeit/Projekte | Projektdatei, Rolle, Target Framework | kein Nachweis tatsächlicher Betriebssysteme/Hosts |
| Schnittstellen | bestehende statische Code-Abhängigkeiten | keine Zusage über Laufzeitkonnektivität |
| Konfiguration | nur Schlüsselnamen und Fundstellen | **keine Werte, Secrets oder URL-Hosts** |
| Deployment-/CI-Deskriptoren (P2) | Dateityp/Pfad der durch den vorhandenen Deployment-Scanner erkannten Dateien (Dockerfile, Compose, Kubernetes, Helm, IaC, CI) | **kein** Import von Service-Details, Konfigurationswerten oder Ausführungsergebnissen |
| Betriebsumgebung, Deployment-/Rollback-Verfahren, Monitoring, Backup, Incident, Rollen, SLA/RTO/RPO | explizite menschliche Quellen | ohne Quelle immer **OFFEN** |

Eigene Angaben können über den vorhandenen `--context`-Vertrag
mit Quellbelegen ergänzt werden (siehe [Product drafts](../cli.md#product-drafts)).
`product check` meldet offene Pflichtabschnitte als `PROD-001`-Warnung;
ein Entwurf bleibt dadurch erstellbar, benötigt aber fachliche Abnahme.

`--ai` ist optional und verwendet ausschließlich die vorhandene lokal/interne
KI-Anbindung. Nur belegte Abschnitte werden formuliert; Sätze müssen bestehende
Evidenz-IDs zitieren. Die KI kann weder RTO/RPO noch organisatorische Regelungen ermitteln,
wenn dafür keine Quellen existieren.

## Implementierung und Qualitätsgrenzen

- `ProductTemplate`: generische Vorlagen verwenden `document_type` statt
  `xt_variant`, `xt_version` und `tailoring`.
  Die Formate sind strikt getrennt; bestehende V-Modell-Templates bleiben unverändert.
- `ProductDraft.Facts`: die zusätzlichen Abschnitte `runtime` und
  `configuration` lesen nur bereits erkannte Fakten. Die vorhandene
  Evidenzbegrenzung auf 40 Elemente je Abschnitt bleibt bestehen: Listen sind
  **nicht zwangsläufig vollständig**.
- `ProductDeploymentFacts`: übernimmt ausschließlich Datei-Metadaten aus
  `DeploymentDescriptors.Is` und der bereits gefilterten Projektdateiliste
  (inkl. `.github` / `.circleci`). Der CodeModel-Graph anderer Dokumenttypen
  bleibt unangetastet. Das separate Kapitel `deployment` bleibt ohne
  menschliche Quellen `OFFEN`, auch wenn Deskriptoren gefunden werden.
  Bei neu hinzugefügten oder entfernten Dateien wird der Evidenzbericht
  durch `product check` als veraltet erkannt; **Inhaltsänderungen** an
  unveränderten Deskriptorpfaden werden in P2 noch nicht bewertet.
- `ProductCheck`: verwendet die vorhandenen Quellen-, Änderungs- und
  KI-Zitatprüfungen; keine neue Prüf-Engine.
- Unit-/Journey-Tests: Templatevalidierung, Offenkennzeichnung, deterministische
  Ausgabe, Schutz der Konfigurationswerte, wiederholbarer CLI-Lauf.

**Nicht enthalten:** detaillierte Auswertung von Docker/Kubernetes/CI-Inhalten
(Services, Images, Jobs, Ports, Umgebung), DOCX, Bewertung von Backup-Erfolg, reale Betriebsmetriken,
Automatisierung von Betriebsentscheidungen oder separate KI-Pipelines.
Diese Erweiterungen benötigen gesonderte Evidenzverträge und reale Pilotfälle.

Qualitätsziele: ISO/IEC 25010 (funktionale Eignung, Zuverlässigkeit, Sicherheit,
Wartbarkeit) sowie ISO/IEC 27001 (Vertraulichkeit, Integrität, Verfügbarkeit).
