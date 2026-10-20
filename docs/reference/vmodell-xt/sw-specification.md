# SW-Spezifikation – Ausfüll- und Reviewhilfe

**Unabhängige Arbeitshilfe; nicht automatisch von DocWizz gerendert.** Die Abschnitts-IDs entsprechen `sw-specification.yaml`.

## Sollinhalte pro Kapitel

| YAML-Abschnitt | Mindestinhalt / konkrete Qualitätssicherung |
|---|---|
| `document-control` | Dokument-ID, SW-Element-ID, Elementebene, Referenz- und Versionsbaseline, Reviewverweise |
| `element-overview` | Funktion, fachliche Verantwortung, Umgebung, Interaktion mit anderen SW-Elementen |
| `scope` | Scope, Nutzer, Ausschlüsse, Annahmen, verfeinernde System-/SW-Spezifikation |
| `derived-requirements` | Eindeutige, atomare IDs mit Eltern-IDs, Priorität, Quelle und Änderungsstand |
| `functional-requirements` | Verbindliche Verhaltensaussage je ID, Eingabe, Vor-/Nachbedingung, Domänenregel, Negativfall |
| `interfaces` | API/Port-ID, Operation, Eingabe- und Ausgabeformat, Schema, AuthZ, Fehlerstatus, Versionierung |
| `behaviour` | Zustands- und Sequenzszenarien, Reihenfolge, Timeout, Nebenläufigkeit und Konsistenz |
| `data-contracts` | DTO-/Schema-Definition, Feldtyp, Wertebereich, Eindeutigkeit, Invarianten, Lebensdauer |
| `nonfunctional-requirements` | Qualitätsziele nach ISO/IEC 25010 als Messgrößen; keine reinen Schlagworte |
| `security-privacy` | CIA-Ziele, Zugriffsmatrix, Schutzbedarf, Datenminimierung und Testkriterien |
| `interface-realization` | Zuordnung spezifizierter Dienste zu untergeordneten SW-Elementen |
| `quality-refinement` | Zerlegung von System-QoS in messbare Komponentenbudgets und Messregeln |
| `error-semantics` | Statuscodes, Ausnahmen, Retry-/Kompensationsregeln, sichere Fehlermeldungen |
| `acceptance-criteria` | Explizite messbare Schwellenwerte oder objektive Zustandsprüfungen je REQ |
| `traceability` | Übergeordnete REQ <-> verfeinerte REQ <-> SW-Element <-> Prüffall |
| `constraints` | Architekturregeln, Regulatorik, freigegebene Laufzeiten, Schnittstellenstandards |
| `glossary` | Terminologie und versionierte Quelle |

## Muster einer atomaren Anforderung

| Feld | Beispiel (fiktiv) |
|---|---|
| Anforderungs-ID | REQ-005 |
| Ursprung | Fachlicher Integritätsbedarf |
| Sollverhalten | Das System muss gleiche Auftragsanlagen desselben Benutzers mit identischem Idempotency-Key als einen logischen Vorgang behandeln. |
| Vorbedingungen | Authentifizierter Benutzer, gültige Auftragseingabe, noch nicht genutzter Key |
| Trigger | Zweiter POST /orders mit identischem Key und Inhalt |
| Erwarteter Zustand | Exakt ein Auftrag gespeichert, dieselbe logische Auftrags-ID wie beim ersten Aufruf |
| Fehlerfall | Gleicher Key mit abweichendem Inhalt wird mit festzulegendem Konfliktstatus abgewiesen |
| Verifikation | TEST-004 mit DB-Prüfung |

## Muster einer überprüfbaren Qualitätsanforderung

**Nicht ausreichend:** „Die Anwendung muss performant, wartbar und sicher sein.“

**Überprüfbar:** „Bei einer festgelegten Datenmenge und 50 parallelen virtuellen Benutzern muss `POST /orders` während eines 10-minütigen stabilen Lastfensters für mindestens 95 % der erfolgreich beantworteten Anfragen eine Antwortzeit von höchstens 400 ms erreichen.“ Ergänzend muss die erlaubte Fehlerrate und die Behandlung fehlgeschlagener Antworten definiert werden.

Bei Anforderungen nach ISO/IEC 25010 ist die jeweils anzuwendende Ausgabe zu prüfen; bei Anwendung von ISO/IEC 27001 ist die konkrete Softwareanforderung aus Sicherheitszielen und relevanten ISMS-Kontrollen abzuleiten.

## Reviewfragen

- Ist jede Anforderung eindeutig, testbar und mit genau einem erwarteten Verhalten formuliert?
- Sind statischer Schnittstellenvertrag und dynamisches Verhalten separat beschrieben?
- Sind Negativfälle und Autorisierungsgrenzen enthalten?
- Sind Qualitätsziele einschließlich Lastprofil, Messmethode und Schwelle messbar?
- Sind abgeleitete Anforderungen bidirektional zu ihrem Ursprung verfolgbar?
- Hat sich die SW-Spezifikation nach einer relevanten Änderung vor der Testanpassung aktualisiert?
