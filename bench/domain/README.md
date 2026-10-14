# Domain discovery benchmark

Does `docwizz domain discover` reach what is on the path from its seed, and only that? This benchmark measures it
against hand-labelled cases (#188). No model and no network.

```bash
python3 bench/domain.py            # Markdown report
python3 bench/domain.py --expect   # exit 1 unless every case matches gold (test.sh runs this)
```

## Dataset

[`../domain_fixture.py`](../domain_fixture.py) builds the requirements benchmark's scripted history (24 methods in 7
services, 20 commits, see [../requirements/README.md](../requirements/README.md)) and adds one unlinked commit with an
API layer: a `CheckoutController` that calls four services, DTO records it constructs, `IPayments` with one
implementation, a `Confirm -> Retry -> Confirm` cycle, and a `ReportsJob` that calls `OrderService.Total` and that
nothing calls. Everything is synthetic.

## Ground truth

[`gold.json`](gold.json) has 25 cases, written by hand from the code: 17 code seeds (entry point, service method, interface method,
implementation, cycle, class seeds, depth 0/1, a 5-symbol budget), 5 work-item seeds (direct link, two methods, PR
only, denied lookup, using-only change) and 3 expected errors (unknown, ambiguous, missing work item). Per case:

- `reach`: symbols on the behaviour path within the depth; they must be in the result.
- `avoid`: symbols that are not: siblings of a reached class, callers, other services, beyond the depth.
- classes and interfaces are unlabelled.

Expected work items on reached methods come from [`../requirements/gold.json`](../requirements/gold.json), not repeated.

## Metrics

| Metric | Measures |
|---|---|
| cases passed | every `reach` symbol found, no `avoid` symbol, truncation, seed symbols and errors as labelled |
| reach recall | labelled path symbols found |
| leak rate | labelled off-path symbols found |
| link precision / recall | (method, work item, status) on path methods against the requirements gold |
| off-path links | work items attached to `avoid` symbols: business evidence from code that isn't on the path |
| reproducible | each case run twice gives identical output |

## Results

| | cases | leak rate | checkout symbols | link precision / recall |
|---|---|---|---|---|
| before (#186) | 18 / 25 | 0.356 | 32 | 0.909 / 1.0 |
| `contains` only from seeds | 25 / 25 | 0.0 | 20 | 0.909 / 1.0 |

Before, a class reached through a field (`orders.Place(...)` reaches `OrderService`) had all of its members expanded:
`Cancel`, `Refund`, `Total`, `Ship` and their work items showed up in a checkout crawl. Now `contains` is followed only
from a seed. The remaining link false positive, `Available` WI-111, is the resolver's (an unrelated edit in a linked
commit, see the requirements benchmark), not the crawler's.

## Domain labels (#187)

`gold.json`'s `domain` block is ground truth for a future domain model, written by hand from the final code:

- **areas:** every method's business area by what it does, not by namespace. `Shop.Api` holds checkout code, but
  also `CardPayments.Charge` (billing) and `CheckoutController.Track` (shipping).
- **processes:** checkout, parcel tracking and the order report, each with an entry point and its steps.
- **rules:** per (work item, method) a verdict: `implemented`, `partial` (does its part, the rest is elsewhere or
  missing), `contradicted` (`Refund` WI-103, `Overdue` WI-106) or `unrelated` (`Available` WI-111).

`--expect` checks that every label names a method that exists, that every method has exactly one area, and that no
label is stale, and that `docwizz domain areas` groups at least as well as namespaces do. The report scores the areas and
gives the baselines a domain model has to beat:

| Measure | Value |
|---|---|
| areas grouped by namespace, pair precision / recall | 0.685 / 0.771 (misplaces the three `Shop.Api` methods above) |
| areas by `docwizz domain areas`, pair precision / recall | **0.906 / 1.0** (misplaces `ReportsJob.Run`: a job that only reads `OrderService.Total` is reporting, not ordering) |
| process steps reached by a crawl from the entry | checkout 9/9, parcel tracking 1/1, order report 1/1 |
| rules shown as As-intended (`--format md`), by verdict | implemented 8/14, partial 3/5, contradicted **2/2**, unrelated **1/1** |

The last row is the limit of the rule-based As-intended section: it shows both contradictions and the unrelated
link as intent, because history can't tell what the code does. A domain model (or model wording, #188) earns its
place by moving those three out of As-intended without losing the implemented ones.
