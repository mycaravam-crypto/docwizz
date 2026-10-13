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
