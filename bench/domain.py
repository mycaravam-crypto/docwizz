#!/usr/bin/env python3
"""Does `docwizz domain discover` reach what is on the path, and only that? Measured against hand-labelled cases (#188).

    python3 bench/domain.py                     # Markdown report to stdout
    python3 bench/domain.py --expect            # exit 1 unless every case matches gold (also in test.sh)

The repository is bench/domain_fixture.py: the requirements benchmark's scripted history plus an API layer (entry
points, DTOs, an interface, a cycle, a caller that must not be walked back into). Ground truth is
bench/domain/gold.json, written by hand; reached methods' expected work items come from bench/requirements/gold.json.

Per case: reach recall (labelled path symbols found), leaks (labelled off-path symbols found), seed symbols, expected
errors and truncation. Overall: work-item links on path symbols (precision and recall over (method, work item,
status)), off-path links (requirements the crawl attached to symbols labelled off the path), byte-identical output
on a second run, symbols and wall time per case. No model, no network.
"""
import argparse
import json
import os
import shutil
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import domain_fixture as fixture  # noqa: E402
from requirements import build, digest, ratio, sh  # noqa: E402

GOLD = os.path.join(HERE, "domain", "gold.json")
REQUIREMENTS_GOLD = os.path.join(HERE, "requirements", "gold.json")


def name(symbol_id):
    """`cs:Shop.Orders.OrderService.Place(int, decimal)` -> `Shop.Orders.OrderService.Place`."""
    return symbol_id.split(":", 1)[-1].split("(")[0]


def discover(dll, repo, case):
    args = ["domain", "discover", ".", "--depth", str(case["depth"]), "--limit", str(case.get("limit", 100)), "--format", "json"]
    args += ["--seed", case["seed"]] if "seed" in case else ["--work-item", str(case["workItem"])]
    start = time.perf_counter()
    p = sh(["dotnet", dll, *args], repo, check=False)
    return p, round((time.perf_counter() - start) * 1000)


def run_case(dll, repos, case, links):
    repo = repos["bare" if case.get("index") is False else "indexed"]
    p, ms = discover(dll, repo, case)
    again, _ = discover(dll, repo, case)
    row = {"name": case["name"], "ms": ms, "reproducible": (p.returncode, p.stdout, p.stderr) == (again.returncode, again.stdout, again.stderr),
           "problems": []}
    if "error" in case:
        row["ok"] = p.returncode != 0 and case["error"] in p.stderr
        if not row["ok"]:
            row["problems"].append(f"expected an error mentioning {case['error']!r}, got exit {p.returncode}: {p.stderr.strip()[:200]}")
        return row, set(), set()
    if p.returncode != 0:
        row["ok"] = False
        row["problems"].append(f"exit {p.returncode}: {p.stderr.strip()[:200]}")
        return row, set(), set()

    report = json.loads(p.stdout)
    got = {name(s["id"]) for s in report["symbols"]}
    reach, avoid = set(case["reach"]), set(case["avoid"])
    row.update(symbols=len(got), reached=len(reach & got), reach=len(reach), leaks=sorted(avoid & got), avoid=len(avoid),
               missed=sorted(reach - got), truncated=report["truncated"])
    if row["missed"]:
        row["problems"].append(f"missed {row['missed']}")
    if row["leaks"]:
        row["problems"].append(f"leaked {row['leaks']}")
    if report["truncated"] != case.get("truncated", False):
        row["problems"].append(f"truncated {report['truncated']}")
    if "seedSymbols" in case and sorted(map(name, report["seed"]["symbols"])) != sorted(case["seedSymbols"]):
        row["problems"].append(f"seed symbols {sorted(map(name, report['seed']['symbols']))}")
    row["ok"] = not row["problems"]

    if case.get("index") is False:
        if report["requirements"] or report["requirementsIndex"]["status"] != "unavailable":
            row["problems"].append("requirements without an index")
            row["ok"] = False
        return row, set(), set()
    # Work-item links: expected on labelled path methods, from the requirements gold; anything on an off-path symbol is noise.
    predicted = {(name(r["symbol"]), r["id"], r["status"]) for r in report["requirements"]}
    expected = {(m, l["id"], l["status"]) for m in reach & got for l in links.get(m, []) if l["status"] != "none"}
    on_path = {x for x in predicted if x[0] in reach and x[0] in links}
    off_path = {x for x in predicted if x[0] in avoid}
    return row, (on_path, expected), off_path


def evaluate(dll, repos, gold, links):
    rows, tp, pred, exp, off, extra, missed = [], 0, 0, 0, set(), set(), set()
    for case in gold["cases"]:
        row, pairs, noise = run_case(dll, repos, case, links)
        rows.append(row)
        if pairs:
            on_path, expected = pairs
            tp, pred, exp = tp + len(on_path & expected), pred + len(on_path), exp + len(expected)
            extra |= {f"{m} WI-{i} {st}" for m, i, st in on_path - expected}
            missed |= {f"{m} WI-{i} {st}" for m, i, st in expected - on_path}
        off |= {(case["name"], *x) for x in noise}
    labelled = [r for r in rows if "reach" in r]
    return {
        "cases": len(rows),
        "passed": sum(r["ok"] for r in rows),
        "reachRecall": ratio(sum(r["reached"] for r in labelled), sum(r["reach"] for r in labelled)),
        "leakRate": ratio(sum(len(r["leaks"]) for r in labelled), sum(r["avoid"] for r in labelled)),
        "linkPrecision": ratio(tp, pred),
        "linkRecall": ratio(tp, exp),
        "linkExtras": sorted(extra),
        "linkMisses": sorted(missed),
        "offPathLinks": len(off),
        "offPathExamples": sorted(f"{c}: {m} WI-{i} {s}" for c, m, i, s in off)[:10],
        "reproducible": all(r["reproducible"] for r in rows),
        "meanSymbols": ratio(sum(r["symbols"] for r in labelled), len(labelled)),
        "meanMs": ratio(sum(r["ms"] for r in rows), len(rows)),
        "rows": rows,
    }


def markdown(result):
    r = result["results"]
    lines = [
        "# Domain discovery benchmark", "",
        f"DocWizz {result['docwizz']}, gold `{result['inputs']['gold']}`, history `{result['inputs']['history']}`.", "",
        "| Metric | Value |", "|---|---|",
        f"| cases passed | {r['passed']} / {r['cases']} |",
        f"| reach recall | {r['reachRecall']} |",
        f"| leak rate (off-path symbols reached) | {r['leakRate']} |",
        f"| work-item link precision / recall on path | {r['linkPrecision']} / {r['linkRecall']} |",
        f"| off-path work-item links | {r['offPathLinks']} |",
        f"| reproducible | {r['reproducible']} |",
        f"| mean symbols / mean ms per case | {r['meanSymbols']} / {r['meanMs']} |", "",
        "| Case | OK | Symbols | Problems |", "|---|---|---|---|",
    ]
    lines += [f"| {x['name']} | {'yes' if x['ok'] else '**no**'} | {x.get('symbols', '')} | {'; '.join(x['problems'])} |" for x in r["rows"]]
    for label, key in (("Links on path that gold doesn't have", "linkExtras"), ("Links on path that are missing", "linkMisses")):
        if r[key]:
            lines += ["", f"{label}:", ""] + [f"- {e}" for e in r[key]]
    if r["offPathExamples"]:
        lines += ["", "Off-path links (first 10):", ""] + [f"- {e}" for e in r["offPathExamples"]]
    return "\n".join(lines) + "\n"


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dll", help="a built DocWizz.dll (default: build Release)")
    ap.add_argument("--out", help="write the JSON result here")
    ap.add_argument("--report", help="write the Markdown report here")
    ap.add_argument("--expect", action="store_true", help="exit 1 unless every case matches gold and the output is reproducible")
    args = ap.parse_args(argv)

    dll = os.path.abspath(build(args.dll))
    gold = json.load(open(GOLD))
    links = {m: g["links"] for m, g in json.load(open(REQUIREMENTS_GOLD))["methods"].items()}
    work = tempfile.mkdtemp(prefix="docwizz-domainbench-")
    try:
        repos = {"indexed": os.path.join(work, "repo")}
        history, items = fixture.build(repos["indexed"], os.path.join(work, "exports"))
        repos["bare"] = os.path.join(work, "bare")
        shutil.copytree(repos["indexed"], repos["bare"])
        sh(["dotnet", dll, "requirements", "import", ".", history, items], repos["indexed"])
        result = {
            "schema": "docwizz.domain-benchmark/v1",
            "docwizz": sh(["dotnet", dll, "--version"], work).stdout.strip(),
            "inputs": {"gold": digest(GOLD), "history": digest(history)},
            "results": evaluate(dll, repos, gold, links),
        }
    finally:
        shutil.rmtree(work, ignore_errors=True)

    if args.out:
        with open(args.out, "w") as f:
            json.dump(result, f, indent=2)
    report = markdown(result)
    if args.report:
        with open(args.report, "w") as f:
            f.write(report)
    print(report)

    if args.expect:
        r = result["results"]
        problems = [f"{x['name']}: {'; '.join(x['problems'])}" for x in r["rows"] if not x["ok"]]
        if not r["reproducible"]:
            problems.append("output differs between two identical runs")
        for p in problems:
            print(f"FAIL: {p}", file=sys.stderr)
        return 1 if problems else 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
