# Load-test reports

[Documentation home](../README.md) · [CLI reference](../cli.md)

## Load-test results

`docwizz load import . shop-bericht.json [more.json ...]` reads [powerwizz](https://github.com/mycaravam-crypto/powerwizz#json-report-for-docwizz)
reports (`powerwizz.report/v1`) natively, offline and without an adapter, and keeps them in `docs/.docwizz/load.json`.
The same run (start time and target) imported again replaces itself; unknown fields are ignored. A field v1 has that is
missing or of another type (`steps[0].error_rate`, `latency_ms.p95`, `started_at`, …) exits 1 naming it, so a missing
number never shows as 0. `generate` then adds **Load (measured)** to `api.md`: per matched endpoint the requests, error
rate and p50/p95/p99 latency of the newest run, one row per step of that run that called it,
with the run as evidence, and the run's label (powerwizz `--label`, e.g. `dev, Debug build`: what was measured),
users, ramp-up, duration and auth once. A run against a non-local target is
marked as not comparable, and a run older than the endpoint file's last commit is marked _stale_.

Each step is matched to a detected endpoint by method, then segment by segment:

- A route parameter (`{id}`, `:id`, `[id]`) matches any one segment, including powerwizz placeholders (`{{vu_id}}`).
  Literal segments must be equal, ignoring case. The query string is ignored.
- The base URL's path prefix (`/app/`) may precede the route; it is stripped when the rest matches.
- The most literal segments win. A tie is ambiguous and not attached; it is listed, like a step with no endpoint.

With more than one run, **Trend** lists per endpoint and step every run against the newest run's target, oldest first:
p95 and error rate, and the newest p95's change from the run before it (`+36.4 %`). Runs against another target aren't
comparable and are left out; nothing judges what counts as a regression. **Timeline** draws each shown run's per-second
`series` (all steps together) as Mermaid line charts of p95 latency and requests per second, at most 60 points: a point
spanning several seconds shows their worst p95, so a spike isn't averaged away. A `series` point without `t`,
`requests` or `p95_ms` exits 1 naming it.
