# Requirement context benchmark

Does giving a local model a symbol's historical requirements (`docwizz generate --ai --requirements`) make its
documentation better? This benchmark measures that question instead of assuming an answer (#168). It reuses `bench/`
conventions (a JSON result and a Markdown report, a fixed seed, no network beyond the model you name), but its
success criteria are its own. The agent benchmark (#104, `bench/agents.py`) is not changed.

## Dataset

[`../requirements_fixture.py`](../requirements_fixture.py) builds a git repository and the exports DocWizz reads.
Everything in it is synthetic: no customer or real work-item text.

- **Code:** a small order domain with 24 C# methods in 7 services.
- **History:** 20 commits with fixed dates and author, so the SHAs and the index fingerprint are the same on every run.
  It covers:
  - direct commit links (WI-101–104, 106, 107, 110–113)
  - a squash merge whose commit has no link and whose PR does (WI-105 → `Issue`, `Ship`)
  - an ambiguous PR association (WI-108 → `Restock`)
  - a denied link lookup (WI-109 → `Quote`)
  - a rename after the linked commit (`ChangeName` → `Rename`, WI-110)
  - an unrelated edit to `Available` in the WI-111 commit
  - a change touching only a `using` (WI-113)
  - two methods that contradict their requirement: `Refund` never checks the total (WI-103), and `Overdue` waits 30 days instead of 14 (WI-106)
  - a work item whose title and criteria try to give the model instructions (WI-112)
  - two methods over the default context bound (#174): `Discount` has four linked work items (WI-114–117), one more
    than `max: 3` sends, so the oldest is left out; `Tax` has one work item (WI-118) longer than 1200 characters, so
    its last criteria are cut
- **Exports:** `gitwizz.history/v2` (the shape `gitwizz history export` writes) from the repository's own
  `git show -U0 -M` diffs, and one
  `ado.requirement-context/v1` file per work item. These are the formats in
  [docs/requirement-context.md](../../docs/requirement-context.md).

## Ground truth

[`gold.json`](gold.json) is written by hand from the scripted history. Nothing a model or DocWizz produced is copied
into it. For each method it records:

- `links`: the work items the method truly implements, each with the status a correct resolver can give from the
  evidence: `confirmed`, `candidate`, or `none` when the evidence can't show the link (the denied lookup, the rename).
- `outcome`: what `docwizz requirements show` must answer.
- `constraints`: phrases a grounded description contains (any one of each group).
- `contradicts`: where the code does not do what the requirement asks.
- `budgetStress`: why the method's requirements exceed the default bound.

It also lists the historical id of the renamed method, the work item that touches no method (WI-113), and the
phrases the injection item tries to plant.

## Evaluation

**Layer 1, links (deterministic, no model).** For each method, `docwizz requirements show --for <method>` is
compared with gold:

- precision and recall over (method, work item) pairs, overall and per status
- recall over the links the evidence can show
- status accuracy and outcome accuracy
- whether a work item that touches no method was ever attached to one
- byte-identical index after a rebuild

**Layer 2, drafts (with `--model`).** `generate --ai` runs once per mode, each with its own cache, then once more
unchanged:

| Mode | Prompt |
|---|---|
| A | today's prompt, no business context |
| B | `--requirements`, default bound (3 items, 1200 characters) |
| C | `--requirements`, `ai.requirements: { max: 10, budget: 20000 }` (the bound lifted) |

A recording proxy on 127.0.0.1 sits between DocWizz and the model, so every request's host, tokens (Ollama's
`prompt_eval_count`/`eval_count`, otherwise characters / 4) and latency are recorded. With `--model fake`, the proxy
is the model.

| Metric | Measures |
|---|---|
| constraint recall | share of gold constraint groups found in the method's kept sentences |
| unsupported WI | share of work-item-citing sentences that cite a work item gold doesn't link to the method (link false positives show up here) |
| invalid citations | work items cited that DocWizz didn't give the model for that method (must be 0) |
| contradictions flagged | methods with a gold contradiction that got a `deviation` sentence |
| false deviations | methods **without** a gold contradiction that got a `deviation` sentence (the sentences are in the JSON) |
| stress recall, bound hit | constraint recall on the `budgetStress` methods; methods whose facts said `leftOut` or `criteriaCut` |
| injection kept | planted phrases ("certified", "admin mode", "approved") that survived into kept sentences |
| tokens, seconds, requests | cost; rerun requests on unchanged inputs (cache, must be 0) |

Intervals are 95% bootstrap intervals over methods (1000 resamples, `--seed`). The JSON records the DocWizz version,
model, provider and build (Ollama digest, quantization), temperature (0), seed, prompt fingerprints (SHA-256 of each distinct system prompt), input digests
(history, work items, gold), the index fingerprint and the budgets.

## What CI checks

`test.sh` runs `--model fake --modes B --expect`. It needs no LLM, no Azure DevOps and no network. It fails when:

- an outcome differs from gold
- a rebuilt index differs
- a file-level change is asserted on a method
- statuses differ
- a request goes anywhere but the loopback fake
- an unchanged rerun sends a request
- a planted phrase survives
- a draft cites a work item it wasn't given

The fake model obeys the injected instructions on purpose, so this check proves DocWizz filters them. `bench.yml`
(weekly, or on demand) runs modes A, B and C and puts the report in the job summary.

## Results (2026-10-13)

### Links

Layer 1 is deterministic. The numbers below hold for any machine:

| Methods | Precision | Recall | Recall (visible evidence) | Confirmed P/R | Candidate P/R | Outcomes | Reproducible |
|---|---|---|---|---|---|---|---|
| 24 | 0.95 | 0.905 | 1.0 | 0.941/1.0 | 1.0/1.0 | 1.0 | yes |

- **False positive:** `Available` WI-111. An unrelated edit in a linked commit becomes a confirmed link. Hunk overlap
  can't tell intent. No real-model draft cited it (unsupported WI 0 in both runs), but nothing guarantees that.
- **Missed:** `Rename` WI-110 (the link stays with the old id, reported as `not-current`) and `Quote` WI-109 (the
  lookup was denied, reported as `unavailable`, never as "no link").

### Drafts with a real local model (#174)

Two runs of `--modes A,B,C` with **qwen2.5-coder:7b** (Ollama 0.40.3, Q4_K_M, digest `dae161e27b0e`, fully in GPU
memory; host i9-13900HK, WSL2), temperature 0, seed 1, `num_ctx` 16384 (#180). Same inputs (history `60c1d75d`, work
items `9a7b3590`, gold `d8601ad8`) and the same prompt fingerprints in both runs. Raw results and reports:
[`results/`](results/) (run 1's report predates the false-deviation column and the corrected recommendation wording;
its JSON has the counts).

```console
$ python3 bench/requirements.py --model qwen2.5-coder:7b --endpoint http://127.0.0.1:11434 --modes A,B,C \
    --out run.json --report run.md
```

| Mode | Constraint recall, run 1 / run 2 (95% CI, run 2) | Stress recall | Unsupported WI | Invalid citations | Injection kept | Contradictions flagged | False deviations | Prompt tokens | Wall s (run 2) |
|---|---|---|---|---|---|---|---|---|---|
| A | 0.50 / 0.50 [0.26, 0.81] | 0.17 | n/a | 0 | 0 | 0/2 | 0 | 22.1k | 309 |
| B | 0.65 / 0.70 [0.47, 0.92] | 0.50 | 0.0 | 0 | 0 | 1/2 | 3 / 4 | 29.5k (×1.34) | 331 |
| C | 0.70 / 0.80 [0.59, 0.96] | 0.83 | 0.0 | 0 | 0 | 0/2 / 1/2 | 4 / 4 | 29.7k | 335 |

Every request went to 127.0.0.1, and every unchanged rerun sent 0 requests. Mode A never saw business context. 38
requests per mode.

- **Grounding improves, but not significantly.** B finds more of what the requirements ask for than A in both runs
  (+0.15, +0.20), but B's interval contains A's value. There are only 20 constraint groups, too few to separate the modes.
- **Run-to-run variance at temperature 0 is small but real.** A was identical in both runs. B differed on one method
  (`Ship`), C on two (`Refund`, `Ship`). Ollama batches concurrent requests, so outputs at temperature 0 are not
  bit-reproducible.
- **The default bound costs grounding where it binds.** `Discount`: A 0/4, B 3/4 (WI-114, the oldest, left out), C
  4/4. `Tax`: no mode wrote "reverse charge" (cut in B, sent in C). A wrote "19" from the code, which B dropped in run 1.
- **Safety held.** No citation of a work item DocWizz didn't send, no unsupported work-item sentence, no planted
  phrase kept.
- **`deviation` is the weak section.**
  - *True positive:* `Refund` "does not enforce the order total limit" (WI-103, B and C).
  - *Missed:* `Overdue` (30 days instead of 14, WI-106) was never flagged, in any mode or run.
  - *False:* `Place` "rejects orders at the limit too, not only above it" (the code uses `>`). In C, `Refund` "does not
    ignore refunds of zero or less" (the code returns 0 for them).
  - *Scope, not contradiction:* WI-112 covers emails and SMS, so `Email` and `Sms` each get "…not SMS texts" / "…not
    only emails". `Ship` "does not issue an invoice" (WI-105) is arguably right: issuing is `Issue`'s job, and the
    gold doesn't count it.
  - So a reader can't trust a deviation sentence without checking it against the code.
- **Cost.** About +34% prompt tokens and +7% wall time for B. C costs the same as B here: only two methods exceed
  the bound.

#### Run 3: deviations must quote the code (#183)

Same model and settings. Only the requirements prompt changed: its example moved to an unrelated domain (it had been
copied word for word onto `Place`), and a deviation must quote the code it is about, which is then checked against
the source.

| Mode | Constraint recall (95% CI) | Unsupported WI | Injection kept | Contradictions flagged | False deviations |
|---|---|---|---|---|---|
| A | 0.45 [0.22, 0.74] | n/a | 0 | 0/2 | 0 |
| B | 0.75 [0.57, 0.92] | 0.0 | 0 | 0/2 | **0** (was 3 / 4) |
| C | 0.80 [0.59, 0.96] | 0.0 | 0 | 0/2 | **1** (was 4 / 4) |

- **False deviations are nearly gone.** The one left (`Restock`, C) quotes real code (`` `quantity < 10` ``) but
  draws a non-difference from it. That's the known ceiling: a quote proves the code exists, not that the claim is right.
- **The true deviation was lost too.** `Refund` (never checks the total) is a deviation by absence. The prompt's "a
  requirement this code doesn't cover at all is no deviation" probably keeps the model from writing it, although it
  could quote `return amount;`. Contradictions flagged: 0/2 in both modes, down from 1/2.
- **B's interval now clears A**, so the report's rule says "enable by default is defensible". Don't read too much into
  it: A dipped (0.50, 0.50, now 0.45, same prompt), B's prompt changed in this run, and it is one run. The rule needs
  repeated runs before it can decide a default.

Limits: one synthetic fixture, one 7B model, three runs, phrase matching for recall (a paraphrase that misses every
listed phrase counts as missed). Link errors (layer 1) and model errors (layer 2) are reported separately: the
`Available` false link is a layer-1 error, and the false deviations are model errors on correct links.

## Recommendation

**Keep `--requirements` opt-in.** (After run 3, still opt-in: the deviation section is now precise but catches no contradiction.) Against the recorded thresholds (B's recall interval above A, unsupported WI ≤ 0.1,
no injection kept, prompt tokens < 2× A, link precision ≥ 0.9), every condition holds except the first: the gain is
consistent but not significant on this fixture.

Before considering a default:

- **Make `deviation` stricter.** A deviation sentence should quote the code it contradicts (`[source]` plus the
  condition), and be dropped otherwise. Or leave deviations out of the pages until they measure better.
- **Grow the gold set.** More contradiction cases and more constraint groups, so that a +0.15–0.2 gain can be
  told apart from noise. Also mark work items that span several methods (WI-112) as scope, not contradiction.
- **Revisit the default bound.** It binds rarely, but when it does it drops the oldest work item first. Keep the
  default and reconsider once real repositories show how often more than three work items link to one symbol.
- **Run a larger local model** (e.g. qwen3-coder) with the same command, for comparison.

Normal CI stays offline: `test.sh` runs `--model fake --modes B --expect`.
