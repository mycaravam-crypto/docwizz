# Software architecture: filling and review guide

[V-Modell XT templates](../../guides/vmodell-xt-templates.md)

A working aid for [sw-architecture.yaml](../../../templates/vmodell-xt/sw-architecture.yaml); docwizz doesn't render
it. The section ids are the template's.

## Expected content per section

| Section | Minimum information / expected evidence | Review question |
|---|---|---|
| `document-control` | identity of the software unit, document revision, source baseline, author and reviewer, linked decision state | Is the draft clearly tied to one version? |
| `scope` | system context, business purpose, users, neighbouring systems, in and out of scope, assumptions | Are responsibilities and system boundaries clear? |
| `architecture-drivers` | requirement ids, prioritized quality scenarios, usage profiles, technical and legal constraints | Can every driving requirement be traced? |
| `principles-alternatives` | architecture principles; at least two plausible options, evaluation criteria, pros and cons | Is the chosen option justified? |
| `decisions` | ADR id, context, decision, alternatives, consequences, risks, evidence of human review | Are trade-offs and impacts documented? |
| `structure` | component hierarchy, ownership, ports, responsibilities, dependencies, intended vs. actual; optional C4 or component view | Do the detected classes match the intended structure? |
| `elements-to-specify` | all relevant software elements, whether each gets its own specification, and why | Is there a specification for every element to be tested? |
| `interfaces` | interface id, provider and consumer, protocol, schema, authentication, version, error contract, availability | Are code dependencies alone enough to interpret it? (No.) |
| `runtime-scenarios` | normal, negative and error flows, timing, timeouts and retries, transaction boundaries, states | Is at least one risk-relevant flow documented? |
| `data-catalog` | data object, type, value ranges, identifiers, protection needs, classification, life cycle | Does it contradict the interface schemas? |
| `crosscutting-concepts` | logging and tracing, configuration, persistence, fault tolerance, consistency, internationalization where needed | Are crosscutting rules enforced system-wide? |
| `security-evidence` | confidentiality, integrity and availability needs, threats, measures, responsible component, test evidence | Are protection goals tied to concrete tests? |
| `deployment-operations` | deployment nodes, data flows, secrets, resources, backup and recovery, monitoring | Are operation and restart questions covered? |
| `design-assurance` | architecture review or prototype results, quality scenarios, criteria, findings, remedies | Are suitability and residual risks assessed verifiably? |
| `traceability` | requirement → building block or ADR → test artifact, with ids and sources | Are any evidence chains missing? |
| `risks-debt` | risk id, likelihood, impact, mitigation, owner, due date | Are open risks visible? |
| `references` | sources, glossary, versioned standards, models | Can every source be found again? |

## Example: architecture drivers with measurable quality attributes

| Id | Quality | Stimulus / setting | System response / measure | Evidence |
|---|---|---|---|---|
| REQ-003 | performance efficiency | 50 concurrent order creations, 10-minute load run | 95th percentile response time ≤ 400 ms | TEST-003, measurement report pending |
| REQ-002 | security / confidentiality | a signed-in user requests another user's order | 403, no sensitive details | TEST-002, run pending |
| REQ-005 | reliability / integrity | a repeated request with the same idempotency key | exactly one booking | TEST-004, run pending |

## Example: decision matrix

| Criterion | HTTP layer with direct database access | Adapter and application layer |
|---|---|---|
| Changeability | strong database coupling | adapters replaceable |
| Testability | tests often need a database | business logic testable in isolation |
| Complexity | lower at first | additional ports needed |
| Decision | rejected | preferred, with reasons (no approval status) |

## Acceptance notes

A generated `structure` list holds at most 40 detected code building blocks and proves neither completeness nor the
target architecture without manual additions. Likewise, `interfaces` shows code dependencies only. Mermaid or C4
diagrams, API contracts, quality budgets and design decisions must be maintained as project sources. The architecture
design review and the decision point need their own evidence.
