# Prüfspezifikation Systemelement – Ausfüll- und Reviewhilfe

**Unabhängige Arbeitshilfe; nicht automatisch von DocWizz gerendert.** Die Abschnitts-IDs entsprechen `system-element-test-specification.yaml`.

## Sollinhalte pro Kapitel

| YAML-Abschnitt | Mindestinhalt / Fachfrage |
|---|---|
| `document-control` | Produkt-ID/Revision, autorisierte Prüferrolle, zu prüfende Baseline, Änderungsstand |
| `test-object` | Eindeutig identifizierbarer Build/Commit, Version, Laufzeitkonfiguration, zugehörige SW-Einheit |
| `test-basis` | REQ-IDs, Spezifikationsrevisionen, Prüfkonzept und Umfang |
| `test-strategy` | Risiko- und anforderungsbasierte Testverfahren; Unit/Integration/API/System/Last/Security als passende Ebenen |
| `risk-priorities` | Risikoauswirkung, Wahrscheinlichkeit, Prüftiefe und begründete Ausschlüsse |
| `test-cases` | Eindeutige TC-ID, Vorbedingung, Testdaten, Schritte, erwartetes Ergebnis, Soll-Orakel, Traceability |
| `test-data` | Synthetische/geschützte Daten, Grenzwerte, Version/Seed, Datenbereinigung |
| `test-environment` | Versionsfestgelegte Systeme/Tools, Dienste, Schnittstellen, Berechtigungen und Reproduzierbarkeit |
| `acceptance` | Eintritt, erwartete Ergebnisse, Pass/Fail, Abbruch- und Abschlusskriterien |
| `coverage` | Matrix REQ -> TC, inklusive Lücken und risikobasierter Abdeckung |
| `security-privacy` | Zugriffsschutz, Testdatenschutz, Isolation, Schadensvorsorge, Nachweise zu CIA |
| `defect-handling` | Defect-ID, Kritikalität, Nachtest, Regression und Entscheidung bei Abweichung |
| `automation` | Runner/Prozedur, Seed, Pipeline, Flaky-Test-Policy, Aufbewahrungsdauer und Ausführungsbeleg |
| `review-criteria` | QS-Checkliste, Prüfer-Beteiligung, bekannte Lücken und menschlicher Freigabeprozess |
| `references` | Prüfrichtlinien, Testkonzepte und validierte Quellstände |

## Muster eines vollständigen Prüffalls

| Attribut | Inhalt (fiktiv, kein ausgeführter Test) |
|---|---|
| Prüf-ID | TEST-004 |
| Verknüpfte Anforderung | REQ-005 |
| Zweck | Sicherstellen, dass eine wiederholte Anlage keine zweite Buchung auslöst |
| Prüfobjekt | orders-api, festzulegender Build-Digest |
| Testverfahren | API + Datenbankintegration |
| Vorbedingung | Authentifizierter Testbenutzer; leere Auftragsmenge; neuer Key K |
| Testdaten | Gültiger Auftrag O, identischer Key K für beide Aufrufe |
| Schritt 1 | POST /orders mit O und K senden |
| Erwartet nach Schritt 1 | Gültige Auftrags-ID A, genau eine persistierte Buchung |
| Schritt 2 | Gleichen Request mit demselben Key K erneut senden |
| Erwartet nach Schritt 2 | Logische Auftrags-ID bleibt A, weiterhin genau eine Buchung |
| Bewertung | Bestanden nur wenn ID und DB-Zustand übereinstimmen |
| Aufzeichnung | Ausführungs-ID, Timestamp, API/DB-Auszug ohne sensible Daten – in **separatem Prüfprotokoll** |

## Abdeckungsmatrix (Sollverknüpfung)

| REQ | TC | Prüfart | Status der Spezifikation | Status der Durchführung |
|---|---|---|---|---|
| REQ-001 | TEST-001 | API/Integration | Entwurf | nicht ausgeführt |
| REQ-002 | TEST-002 | Security/API | Entwurf | nicht ausgeführt |
| REQ-003 | TEST-003 | Performance | Entwurf | nicht ausgeführt |
| REQ-004 | TEST-002 | Security | Entwurf | nicht ausgeführt |
| REQ-005 | TEST-004 | Integration | Entwurf | nicht ausgeführt |

## Wichtige Trennung der Nachweise

Die **Prüfspezifikation** definiert vorab Ziele, Bedingungen, Schritte und Bewertungskriterien. Die **Prüfprozedur** beschreibt die reproduzierbare Ausführung; das **Prüfprotokoll** dokumentiert die tatsächlich erzielten Ergebnisse für einen eindeutig identifizierten Build. Eine Zuordnung `REQ -> TEST` – auch eine von `docwizz trace` erfolgreiche – belegt **keine** erfolgreiche Testausführung.

## Reviewfragen

- Eindeutiger Sollzustand und Entscheidung bei jeder Abweichung?
- Reproduzierbare, datenschutzkonforme Testdaten und Testumgebung?
- Vollständige Zuordnung der relevanten Anforderungen?
- Risikobasierte negative und nichtfunktionale Fälle vorhanden?
- Ist der Nachweis des Prüflaufs eindeutig vom Entwurf getrennt?
