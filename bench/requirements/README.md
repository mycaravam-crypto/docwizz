# Requirement context benchmark

Does giving a local model a symbol's historical requirements (`docwizz generate --ai --requirements`) make its
documentation better? This benchmark measures that question instead of assuming an answer (#168). It reuses `bench/`
conventions (a JSON result and a Markdown report, a fixed seed, no network beyond the model you name), but its
success criteria are its own. The agent benchmark (#104, `bench/agents.py`) is not changed.

## Dataset

[`../requirements_fixture.py`](../requirements_fixture.py) builds a git repository and the exports DocWizz reads.
Everything in it is synthetic: no customer or real work-item text.

- **Code:** a small order domain with 22 C# methods in 6 services.
- **History:** 15 commits with fixed dates and author, so the SHAs and the index fingerprint are the same on every run.
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
| injection kept | planted phrases ("certified", "admin mode", "approved") that survived into kept sentences |
| tokens, seconds, requests | cost; rerun requests on unchanged inputs (cache, must be 0) |

Intervals are 95% bootstrap intervals over methods (1000 resamples, `--seed`). The JSON records the DocWizz version,
model and provider, temperature (0), seed, prompt fingerprints (SHA-256 of each distinct system prompt), input digests
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

Layer 1 is deterministic. The numbers below hold for any machine:

| Methods | Precision | Recall | Recall (visible evidence) | Confirmed P/R | Candidate P/R | Outcomes | Reproducible |
|---|---|---|---|---|---|---|---|
| 22 | 0.933 | 0.875 | 1.0 | 0.917/1.0 | 1.0/1.0 | 1.0 | yes |

- **False positive:** `Available` WI-111. An unrelated edit in a linked commit becomes a confirmed link. Hunk overlap
  can't tell intent; the drafts show the cost (unsupported WI 0.06 in mode B, all from this one link).
- **Missed:** `Rename` WI-110 (the link stays with the old id, reported as `not-current`) and `Quote` WI-109 (the
  lookup was denied, reported as `unavailable`, never as "no link").

Layer 2 has only been run with the fake model so far: **the models that would answer the question could not be
reached from the environment this was built in.** The fake run shows that the pipeline holds its rules:

- no requests leave loopback
- an unchanged rerun sends nothing
- no invalid citations
- the injected "certified/approved" sentences are dropped
- mode A never sees business context

It shows **nothing** about documentation quality: the fake copies criteria by construction. Mode C equals B on this
fixture, because no method has more than two linked items, so the bound never binds.

## Recommendation

**Keep `--requirements` opt-in.**

- Link precision is high, but not high enough to apply silently. Co-changed methods inherit links, and that is
  exactly the unsupported-claim risk the drafts must not carry.
- The question that decides the default (does a real local model write more grounded docs with B than with A, at
  what token cost, without more unsupported claims?) has not been measured yet.

To answer it, run this on a machine with a local model:

```console
$ python3 bench/requirements.py --model qwen2.5-coder:7b --endpoint http://127.0.0.1:11434 --modes A,B,C \
    --out bench/requirements/run.json --report bench/requirements/run.md
```

The report's recommendation line applies the rule: enable by default only if B's constraint recall beats A's with
its lower interval bound above A, unsupported WI ≤ 0.1, no injection phrase kept, prompt tokens under 2× A, and link
precision ≥ 0.9. Otherwise keep it opt-in, or revise it.
