# Software specification: filling and review guide

[V-Modell XT templates](../../guides/vmodell-xt-templates.md)

A working aid for [sw-specification.yaml](../../../templates/vmodell-xt/sw-specification.yaml); docwizz doesn't render
it. The section ids are the template's.

## Expected content per section

| Section | Minimum content / quality assurance |
|---|---|
| `document-control` | document id, software element id, element level, reference and version baseline, review references |
| `element-overview` | function, business responsibility, environment, interaction with other software elements |
| `scope` | scope, users, exclusions, assumptions, the system or software specification it refines |
| `derived-requirements` | unique, atomic ids with parent ids, priority, source and change state |
| `functional-requirements` | one binding behaviour statement per id, input, pre- and postconditions, domain rule, negative case |
| `interfaces` | API or port id, operation, input and output format, schema, authorization, error status, versioning |
| `behaviour` | state and sequence scenarios, ordering, timeouts, concurrency and consistency |
| `data-contracts` | DTO or schema definition, field type, value range, uniqueness, invariants, lifetime |
| `nonfunctional-requirements` | quality goals per ISO/IEC 25010 as measures, not keywords |
| `security-privacy` | confidentiality, integrity and availability goals, access matrix, protection needs, data minimization, test criteria |
| `interface-realization` | which subordinate software elements provide the specified services |
| `quality-refinement` | system quality-of-service goals broken down into measurable component budgets and measurement rules |
| `error-semantics` | status codes, exceptions, retry and compensation rules, safe error messages |
| `acceptance-criteria` | explicit measurable thresholds or objective state checks per requirement |
| `traceability` | parent requirement ↔ refined requirement ↔ software element ↔ test case |
| `constraints` | architecture rules, regulation, approved runtimes, interface standards |
| `glossary` | terms and their versioned source |

## Pattern: an atomic requirement

| Field | Example (fictitious) |
|---|---|
| Requirement id | REQ-005 |
| Origin | business integrity need |
| Required behaviour | The system must treat identical order creations by the same user with the same idempotency key as one logical operation. |
| Preconditions | signed-in user, valid order input, unused key |
| Trigger | a second `POST /orders` with the same key and content |
| Expected state | exactly one stored order, with the same logical order id as the first call |
| Error case | the same key with different content is rejected with a conflict status still to be defined |
| Verification | TEST-004 with a database check |

## Pattern: a verifiable quality requirement

**Not enough:** "The application must be fast, maintainable and secure."

**Verifiable:** "With a defined data volume and 50 concurrent virtual users, `POST /orders` must answer at least 95 % of
successful requests within 400 ms during a stable 10-minute load window." Also define the allowed error rate and how
failed responses are counted.

For ISO/IEC 25010 requirements, check which edition applies; under ISO/IEC 27001, derive the concrete software
requirement from the security goals and the relevant ISMS controls.

## Review questions

- Is each requirement unambiguous, testable and phrased with exactly one expected behaviour?
- Are the static interface contract and the dynamic behaviour described separately?
- Are negative cases and authorization boundaries included?
- Are quality goals measurable, including load profile, method and threshold?
- Can derived requirements be traced to their origin in both directions?
- After a relevant change, was the specification updated before the tests?
