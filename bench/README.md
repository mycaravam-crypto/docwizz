# Benchmarks

How long DocWizz takes and how much memory it needs as a repository grows, measured reproducibly so a regression
shows up before users notice it.

```console
$ python3 bench/run.py                                    # small + medium → table, bench/results.json
$ python3 bench/run.py --sizes small,medium,large --compare bench/baseline.json
```

Needs the .NET 10 SDK, Node 22 and Python 3. The runner builds DocWizz in Release, generates each repository with
[synth.py](synth.py), and runs `scan`, `analyze` and `generate` on it with `--timings`. Per command it records wall
time, peak memory of the whole process tree (the Node scanner included), the time per stage, and the model's node and
edge counts.

## The repositories

[synth.py](synth.py) writes the same files for the same size, so runs compare across machines. A module is a vertical
slice: an ASP.NET controller (3 endpoints) → service + interface → repository → one shared EF Core DbContext, an entity,
a test, a TypeScript API client and a Vue component that calls the endpoints. Every fourth module adds a Spring slice
(controller, service, JPA repository, entity) and SQL (a migration and a procedure). Services call the next module's
service, so the graph has cross-module edges and flows, not isolated islands.

| Size | Modules | Files | Nodes | Edges | Endpoints |
|---|---|---|---|---|---|
| small | 10 | 103 | 331 | 665 | 33 |
| medium | 100 | 955 | 3,183 | 6,531 | 325 |
| large | 400 | 3,805 | 12,708 | 26,106 | 1,300 |

## Baseline

[baseline.json](baseline.json), measured on 2026-10-06: Linux x86_64 container, 4 vCPUs (Intel Xeon @ 2.80 GHz),
16 GB RAM, .NET SDK 10.0.112, Node 22.22, Python 3.13. A GitHub `ubuntu-latest` runner has the same vCPU count and
memory, but shared runners vary by tens of percent from run to run.

| Size | scan | analyze | generate | Peak memory (generate) |
|---|---|---|---|---|
| small | 4.7 s | 4.2 s | 4.7 s | 208 MB |
| medium | 7.5 s | 8.5 s | 10.4 s | 265 MB |
| large | 15.1 s | 15.2 s | 27.4 s | 315 MB |

Stages of `generate`, in ms:

| Size | files | scan:csharp | scan:frontend | scan:sql | scan:java | link | analyze | architecture | generate |
|---|---|---|---|---|---|---|---|---|---|
| small | 30 | 2,646 | 1,003 | 38 | 99 | 23 | 44 | 24 | 396 |
| medium | 42 | 6,505 | 1,149 | 33 | 167 | 58 | 122 | 56 | 1,832 |
| large | 60 | 13,179 | 2,073 | 59 | 242 | 272 | 379 | 195 | 10,294 |

The wall times include about 1 s of process start-up that the stages don't show.

## What the numbers say

- **The C# scan dominates small and medium repositories.** Roslyn has a fixed cost of about 2.5 s, then adds roughly
  4 ms per C# file (medium to large).
- **Page generation is the one stage that grows faster than the repository.** From medium to large (4× the modules),
  `generate` takes 5.6× as long. Every endpoint, route and module page traces its flow through the graph, and every
  module page looks up the flows that pass through it. This stage is what to profile first if a large repository is
  slow.
- **Memory stays modest.** Peak memory is about 200 MB plus roughly 30 MB per 1,000 files, mostly Roslyn's syntax
  trees and semantic model.
- **The frontend scanner** is one Node process for all files, and costs about 1 s plus roughly 1.4 ms per file.
- **`diff` and `check --since`** scan two trees, so expect about twice `analyze` (not measured separately).

## Regression detection

`--compare bench/baseline.json` exits 1 when a command takes more than `--tolerance` (default 2.0×) the baseline's time
or memory, or when the model size moves more than 25% (synth.py or a scanner changed what it emits: regenerate the
baseline on purpose). The threshold is deliberately loose. Shared runners are noisy, and the goal is to catch an
accidentally quadratic loop, not a 10% drift. Normal PR CI never runs it. [bench.yml](../.github/workflows/bench.yml)
runs it on demand and weekly, and writes the table to the job summary.

To move the baseline after an intended change, run
`python3 bench/run.py --sizes small,medium,large --out bench/baseline.json`, update the tables above, and commit both.

## Large repositories and monorepos

Known limits:

- **One model per run.** All C# files are compiled into one Roslyn compilation, and the graph and pages are built in
  memory. There is no incremental scan; only page *writing* is incremental.
- **Generation is superlinear** in endpoints × reachable graph (see above). Extrapolating from the baseline, a full
  `generate` takes a minute or more at about 10,000 files.
- **Diagrams are capped.** Module graphs over 120 edges are listed instead of drawn.

Recommended configuration:

- **Scan per service, not the whole monorepo**: `docwizz check services/orders --since origin/main`. Each service gets
  its own `docwizz.yaml`, layers and thresholds, and the GitHub Action's `path:` input does the same in CI.
- **Exclude what isn't yours**: generated clients, vendored code, migration snapshots and large fixtures, via
  `exclude: ["*/Generated/*", "*/Migrations/*.Designer.cs", "vendor/*"]`. Excluded files are never read.
- **Keep test globs accurate** (`tests:`). Test code is scanned only to link tests, but a test tree that isn't matched
  is analyzed and documented as production code.
- **Generate docs where they're read.** `generate` is the expensive command. Run it on the default branch, and keep
  `check --since` for pull requests: it doesn't render pages.

## Coding agents with and without docwizz

Does docwizz context make a local coding agent better? [agents.py](agents.py) measures it instead of claiming it
(issue #104). It runs a fixed task list against fresh git copies of `fixture/` and `fixture-legacy/`, in three variants:

| Variant | The agent gets |
|---|---|
| `none` | the repository |
| `docs` | the repository, `docs/` from `docwizz generate`, and the [AGENTS.md template](../templates/AGENTS.md) |
| `context` | as `docs`, plus the instruction to use `docwizz context` (and `docwizz mcp`, where the harness supports MCP) |

The [tasks](agents/tasks.json) (10) are documentation fixes, architecture fixes (ARCH-001, ARCH-002), a new endpoint
in the existing pattern, and questions about the code (which external systems an endpoint reaches, who calls an
endpoint, which test exercises a method, which configuration key a class reads). Each has **executable success
checks**: docwizz's own analysis of the result (the symbol is documented, the violation is gone, the endpoint exists and
reaches the service, `check --since HEAD` passes) or the answer in `ANSWER.md` (expected terms present, wrong ones
absent). The agent's own claim never counts.

Per run it records success, wall time, and what the harness reports: steps (LLM calls), prompt and completion tokens,
files read. Several runs per variant (`--runs 3`) show the spread; model, harness version and temperature are recorded.

```console
$ python3 bench/agents.py --agent reference --expect all     # self-test: every check passes on the known solution
$ python3 bench/agents.py --agent noop --expect none         # self-test: nothing passes when nothing is done
$ python3 bench/agents.py --agent aider --model openai/qwen3-coder --endpoint http://127.0.0.1:8000/v1 --runs 3 --report report.md
$ python3 bench/agents.py --agent command --runs 3 \
    --cmd 'opencode run -m local/qwen3-coder "$(cat {prompt_file})"'
```

`--agent aider` runs [Aider](https://aider.chat) headless against an OpenAI-compatible endpoint (or natively against
Ollama with `ollama_chat/<model>`) and reads its token counts. **Headless Aider is one-shot, not an agent loop:**
`--message-file` sends the prompt, applies the reply's edits and exits. It doesn't run commands (so it never calls
`docwizz context`), reads only files it asks for in that one reply, and stops after announcing what it would do. That
measures single-reply editing, not the question: the `context` variant can't work, and question tasks mostly end
without an `ANSWER.md`. For the `docs`/`context` comparison use a harness that loops over tool calls (`--agent command`
with OpenCode or similar). First measurement, 2026-10-15 (Aider 0.86.2, qwen2.5-coder:7b, 1 run): none 1/10, docs
2/10, context 2/10, 0 files read in every run; details in #104. `--agent command` runs any harness: `{prompt_file}` is the prompt, and the command may write steps and token
counts to `{metrics_file}` as JSON. In the `docs` and `context` variants a `docwizz` command is on the agent's `PATH`,
and `DOCWIZZ_MCP_COMMAND` holds the command line of the MCP server for harness configs. Results go to
`bench/agents-results.json` and a Markdown report to stdout.

A real run takes an hour or more on a local model, so it is never part of CI. The two self-tests take about two
minutes and run weekly with the other benchmarks ([bench.yml](../.github/workflows/bench.yml)): they prove that every
task is solvable and that no check passes by accident.

## MCP session reuse (#204)

For a reproducible before/after comparison without a local SDK, manually start the GitHub Actions
[`mcp-performance` workflow](../.github/workflows/mcp-performance.yml) on the default branch.
It builds the commit immediately before #205 and the checked-out version on the **same runner**,
executes both on the medium/large fixtures, and publishes the resulting tables in the job summary
and as an artifact. The workflow is opt-in only; it does not gate ordinary pull requests.

```console
$ npm ci --prefix scanner-vue
$ python3 bench/mcp.py --sizes medium,large             # --repeats 5 by default, --dll <old DocWizz.dll> for a baseline
```

Measures repeated calls in the *same* `docwizz mcp` process, where the session caches apply. For each deterministic
fixture it commits the generated repository, adds one uncommitted source edit, starts the server and times the
`initialize` answer as **startup** (process start and model load). Then it calls `impact(HEAD)` and `check` once each,
followed by `--repeats` more calls, and reports the first call and the median of the repeats. The last column lists
the `model.source` each tool's answers reported, so a repeat that rescanned (or a stale model) is visible instead of
passing for a cache hit. Server stderr goes to a file, so a chatty scan cannot block the run.

To compare against an older version, build it separately and pass its assembly with `--dll /path/to/old/DocWizz.dll`.
Use the **same machine and fixture sizes** for both. This is a manual diagnostic, not a CI gate: shared-runner timing
variance must not fail unrelated pull requests. Post measured numbers on issue #204 rather than replacing the CLI scan
baseline above with estimated values.

## Requirement context

```console
$ python3 bench/requirements.py                      # links against gold: no model needed
$ python3 bench/requirements.py --model fake --expect  # + the drafting pipeline against a fake model (test.sh runs this)
$ python3 bench/requirements.py --model qwen2.5-coder:7b --endpoint http://127.0.0.1:11434 --modes A,B,C --out r.json --report r.md
```

Measures whether historical business context (`docwizz requirements`, `--requirements`) improves what a local model
writes, rather than assuming it. Method, ground truth and the current numbers: [requirements/README.md](requirements/README.md).

## Domain discovery

```console
$ python3 bench/domain.py            # 25 hand-labelled crawls: reach, leaks, work-item links
$ python3 bench/domain.py --expect   # exit 1 unless every case matches gold (test.sh runs this)
```

Measures whether `docwizz domain discover` reaches what is on the path from its seed, and only that. No model.
Method, ground truth and the current numbers: [domain/README.md](domain/README.md).
