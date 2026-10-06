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
