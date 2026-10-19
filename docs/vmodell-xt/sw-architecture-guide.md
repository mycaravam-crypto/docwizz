# SW-Architektur – Ausfüll- und Reviewhilfe

**Unabhängige Arbeitshilfe; nicht vom DocWizz-Schema automatisch gerendert.** Die Abschnitts-IDs entsprechen `sw-architecture.yaml`.

## Sollinhalte pro Kapitel

| YAML-Abschnitt | Konkrete Mindestinformation / erwarteter Nachweis | Reviewfrage |
|---|---|---|
| `document-control` | Identifikation der SW-Einheit, Dokumentrevision, Quellbaseline, Autor/Reviewer, verknüpfter Entscheidungsstand | Ist der Entwurf eindeutig einer Version zugeordnet? |
| `scope` | Systemkontext, fachlicher Zweck, Nutzer, Nachbarsysteme, In/Out-Scope, Annahmen | Sind Verantwortlichkeiten und Systemgrenzen klar? |
| `architecture-drivers` | REQ-IDs, priorisierte Qualitätsszenarien, Nutzungsprofile, technische/rechtliche Randbedingungen | Ist jede treibende Anforderung nachvollziehbar? |
| `principles-alternatives` | Architekturprinzipien; mindestens zwei plausible Optionen, Bewertungsmaßstab, Vor-/Nachteile | Wurde eine Option nachvollziehbar begründet? |
| `decisions` | ADR-ID, Kontext, Entscheidung, Alternativen, Konsequenzen, Risiken, menschlicher Reviewbeleg | Sind Trade-offs und Auswirkungen dokumentiert? |
| `structure` | Komponentenhierarchie, Ownership, Ports, Verantwortungen, Abhängigkeiten, Soll-/Ist-Abgleich; C4/Komponentensicht optional | Stimmen erkannte Klassen mit der beabsichtigten SW-Struktur überein? |
| `elements-to-specify` | Liste aller relevanten SW-Elemente, Ja/Nein zur separaten Spezifikation, begründete Entscheidung | Gibt es für zu prüfende Elemente Spezifikationen? |
| `interfaces` | Schnittstellen-ID, Provider/Consumer, Protokoll, Schema, Auth, Version, Fehlervertrag, Verfügbarkeit | Reichen Code-Abhängigkeiten allein zur Interpretation? (Nein.) |
| `runtime-scenarios` | Normal-/Negativ-/Fehlerabläufe, Timing, Timeout/Retry, Transaktionsgrenze, Zustände | Ist wenigstens ein risikorelevanter Ablauf dokumentiert? |
| `data-catalog` | Datenobjekt, Typ, Wertebereiche, Identifikatoren, Schutzbedarf, Klassifikation, Lebenszyklus | Gibt es Widersprüche zu den Schnittstellenschemata? |
| `crosscutting-concepts` | Logging/Tracing, Konfiguration, Persistenz, Fehlertoleranz, Konsistenz, Internationalisierung bei Bedarf | Werden Querschnittsregeln systemweit durchgesetzt? |
| `security-evidence` | CIA-Schutzbedarf, Bedrohungen, Maßnahmen, verantwortliche Komponente, Prüfnachweise | Sind Schutzziele mit konkreten Prüfungen verbunden? |
| `deployment-operations` | Deployment-Knoten, Datenflüsse, Secrets, Ressourcen, Backup/Recovery, Überwachung | Sind Betriebs- und Wiederanlauffragen abgedeckt? |
| `design-assurance` | Architektur-Review-/Prototyp-Ergebnisse, Qualitätsszenarien, Kriterien, Befunde, Abhilfen | Sind Eignung und Restrisiken prüfbar bewertet? |
| `traceability` | REQ -> Architekturbaustein / ADR -> Prüfartefakt (mit IDs und Quellen) | Fehlen Nachweisketten? |
| `risks-debt` | Risiko-ID, Wahrscheinlichkeit, Auswirkung, Gegenmaßnahme, Eigentümer, Termin | Sind offene Risiken transparent? |
| `references` | Quellenbasis, Glossar, versionierte Normen, Modelle | Sind Quellen reproduzierbar auffindbar? |

## Beispiel: Architekturtreiber mit messbaren Qualitätsmerkmalen

| ID | Qualität | Stimulus / Rahmen | Systemreaktion / Messgröße | Nachweis |
|---|---|---|---|---|
| REQ-003 | Leistungseffizienz | 50 parallele Auftragsanlagen, 10-minütiger Lastlauf | 95. Perzentil Antwortzeit <= 400 ms | TEST-003, Messbericht noch ausstehend |
| REQ-002 | Sicherheit / Vertraulichkeit | Authentifizierter Benutzer fragt fremden Auftrag ab | 403, keine sensiblen Details | TEST-002, Ausführung noch ausstehend |
| REQ-005 | Zuverlässigkeit / Integrität | Doppelter Request mit gleichem Idempotency-Key | Nur eine Buchung | TEST-004, Ausführung noch ausstehend |

## Beispiel: Entscheidungsmatrix

| Kriterium | HTTP-Schicht mit direktem DB-Zugriff | Adapter + Anwendungsschicht |
|---|---|---|
| Änderbarkeit | DB-Kopplung hoch | Adapter austauschbar |
| Testbarkeit | Test mit DB oft erforderlich | Fachlogik isoliert prüfbar |
| Komplexität | niedriger initial | zusätzliche Ports erforderlich |
| Entscheidung | verworfen | begründet bevorzugt (kein Freigabestatus) |

## Fachliche Abnahmehinweise

Eine erzeugte `structure`-Liste enthält maximal 40 automatisch erkannte Code-Bausteine und kann ohne manuelle Ergänzung weder Vollständigkeit noch die Zielarchitektur belegen. Analog bilden `interfaces` nur Code-Abhängigkeiten ab. Mermaid-/C4-Diagramme, API-Verträge, Qualitätsbudgets und Designentscheidungen müssen als projektbezogene Quellen gepflegt werden. Architekturdesign-Review und Entscheidungspunkt sind separat nachweisbar zu machen.
