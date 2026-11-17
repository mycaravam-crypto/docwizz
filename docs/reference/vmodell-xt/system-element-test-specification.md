# System-element test specification: filling and review guide

[V-Modell XT templates](../../guides/vmodell-xt-templates.md)

A working aid for [system-element-test-specification.yaml](../../../templates/vmodell-xt/system-element-test-specification.yaml);
docwizz doesn't render it. The section ids are the template's.

## Expected content per section

| Section | Minimum content / question |
|---|---|
| `document-control` | product id and revision, authorized tester role, baseline under test, change state |
| `test-object` | uniquely identified build or commit, version, runtime configuration, software unit |
| `test-basis` | requirement ids, specification revisions, test concept and scope |
| `test-strategy` | risk- and requirement-based test methods; unit, integration, API, system, load and security as fitting levels |
| `risk-priorities` | risk impact, likelihood, test depth and justified exclusions |
| `test-cases` | unique test-case id, precondition, test data, steps, expected result, oracle, traceability |
| `test-data` | synthetic or protected data, boundary values, version or seed, clean-up |
| `test-environment` | version-pinned systems and tools, services, interfaces, permissions, reproducibility |
| `acceptance` | entry, expected results, pass/fail, abort and exit criteria |
| `coverage` | requirement → test-case matrix, including gaps and risk-based coverage |
| `security-privacy` | access protection, test data protection, isolation, damage prevention, confidentiality/integrity/availability evidence |
| `defect-handling` | defect id, severity, retest, regression and the decision on a deviation |
| `automation` | runner or procedure, seed, pipeline, flaky-test policy, retention and evidence of the run |
| `review-criteria` | QA checklist, reviewer involvement, known gaps and the human approval process |
| `references` | test guidelines, test concepts and validated source versions |

## Pattern: a complete test case

| Attribute | Content (fictitious, not a run test) |
|---|---|
| Test id | TEST-004 |
| Linked requirement | REQ-005 |
| Purpose | make sure a repeated creation causes no second booking |
| Test object | orders-api, build digest to be defined |
| Method | API and database integration |
| Precondition | signed-in test user; no orders; a new key K |
| Test data | a valid order O, the same key K for both calls |
| Step 1 | send `POST /orders` with O and K |
| Expected after step 1 | a valid order id A, exactly one stored booking |
| Step 2 | send the same request with the same key K again |
| Expected after step 2 | the logical order id is still A, still exactly one booking |
| Verdict | passed only if the id and the database state match |
| Record | run id, timestamp, API and database extract without sensitive data, in a **separate test log** |

## Coverage matrix (intended links)

| Requirement | Test case | Kind | Specification status | Run status |
|---|---|---|---|---|
| REQ-001 | TEST-001 | API/integration | draft | not run |
| REQ-002 | TEST-002 | security/API | draft | not run |
| REQ-003 | TEST-003 | performance | draft | not run |
| REQ-004 | TEST-002 | security | draft | not run |
| REQ-005 | TEST-004 | integration | draft | not run |

## Keep the evidence apart

The **test specification** defines goals, conditions, steps and criteria in advance. The **test procedure** describes
the reproducible execution; the **test log** records the results actually achieved for one identified build. A
requirement → test link, even one `docwizz trace` reports as complete, is **no** evidence of a passed test.

## Review questions

- Is there a clear expected state, and a decision for every deviation?
- Are test data and environment reproducible and privacy-compliant?
- Are all relevant requirements covered?
- Are risk-based negative and non-functional cases included?
- Is the evidence of the test run clearly separated from the specification?
