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
on a second run, symbols and wall time per case. Domain labels (#187: areas, processes, rules) are checked against the
scanned model, and the baselines a domain model has to beat are reported. No model, no network.
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


def intended(r):
    """The Markdown's As-intended rule: a confirmed link on unchanged code, to an exported, current work item."""
    return r["status"] == "confirmed" and not r["changedSince"] and r["availability"] == "available" and r.get("isCurrent") is not False


def pairs(groups):
    """Every unordered pair of methods that share a group."""
    return {tuple(sorted((a, b))) for g in groups for a in g for b in g if a < b}


def domain(dll, repo, gold, work):
    """Domain labels (#187): checked against the scanned model, plus the baselines a domain model has to beat."""
    d, problems = gold["domain"], []
    model = os.path.join(work, "model.json")
    sh(["dotnet", dll, "scan", ".", model], repo)
    methods = {name(n["id"]) for n in json.load(open(model))["nodes"] if n["kind"] == "method" and n["file"].startswith("src/")}
    labelled = [m for ms in d["areas"].values() for m in ms]
    problems += [f"area label for a method that doesn't exist: {m}" for m in sorted(set(labelled) - methods)]
    problems += [f"method without an area: {m}" for m in sorted(methods - set(labelled))]
    problems += [f"method in more than one area: {m}" for m in sorted({m for m in labelled if labelled.count(m) > 1})]
    named = [p["entry"] for p in d["processes"]] + [s for p in d["processes"] for s in p["steps"]] + [r["method"] for r in d["rules"] if r["method"]]
    problems += [f"process or rule names a method that doesn't exist: {m}" for m in sorted(set(named) - methods)]

    # Areas: the trivial grouping by namespace (Shop.Orders, Shop.Api, ...).
    gold_pairs = pairs(d["areas"].values())
    by_namespace = {}
    for m in methods:
        by_namespace.setdefault(m.rsplit(".", 2)[0], []).append(m)
    base_pairs = pairs(by_namespace.values())
    # A method is misplaced by namespace when its namespace isn't the one most of its gold area shares.
    misplaced = []
    for ms in d["areas"].values():
        spaces = [m.rsplit(".", 2)[0] for m in ms]
        usual = max(spaces, key=spaces.count)
        misplaced += [m for m, ns in zip(ms, spaces) if ns != usual]
    areas = {"precision": ratio(len(base_pairs & gold_pairs), len(base_pairs)), "recall": ratio(len(base_pairs & gold_pairs), len(gold_pairs)),
             "misplaced": sorted(misplaced)}
    # The model: `docwizz domain areas` (folder plus delegation and interface evidence). Misplaced: in a group whose
    # gold area most of the group doesn't share.
    out = sh(["dotnet", dll, "domain", "areas", ".", "--format", "json"], repo)
    got = [[name(m["id"]) for m in a["members"]] for a in json.loads(out.stdout)["areas"]]
    area_of = {m: a for a, ms in d["areas"].items() for m in ms}
    model_pairs = pairs(got)
    wrong = sorted(m for g in got for m in g
                   if area_of.get(m) != max((area_of.get(x) for x in g), key=[area_of.get(x) for x in g].count))
    model = {"precision": ratio(len(model_pairs & gold_pairs), len(model_pairs)), "recall": ratio(len(model_pairs & gold_pairs), len(gold_pairs)),
             "misplaced": wrong}
    if model["precision"] < areas["precision"] or model["recall"] < areas["recall"]:
        problems.append(f"domain areas no better than namespaces: {model['precision']}/{model['recall']} vs {areas['precision']}/{areas['recall']}")

    # Processes: what a crawl from the entry reaches of the labelled steps.
    processes = []
    for p in d["processes"]:
        out, _ = discover(dll, repo, {"seed": p["entry"], "depth": 4})
        got = {name(s["id"]) for s in json.loads(out.stdout)["symbols"]} if out.returncode == 0 else set()
        processes.append({"name": p["name"], "steps": len(p["steps"]), "reached": len(set(p["steps"]) & got)})

    # Rules: what the As-intended section would show for each labelled (work item, method), by verdict.
    verdicts = {(r["method"], r["workItem"]): r["verdict"] for r in d["rules"] if r["method"]}
    shown = {}
    for m in sorted({m for m, _ in verdicts}):
        out, _ = discover(dll, repo, {"seed": m, "depth": 0})
        for r in json.loads(out.stdout)["requirements"] if out.returncode == 0 else []:
            if (m, r["id"]) in verdicts and intended(r):
                shown[(m, r["id"])] = verdicts[(m, r["id"])]
    by_verdict = {v: {"labelled": sum(x == v for x in verdicts.values()), "asIntended": sum(x == v for x in shown.values())}
                  for v in sorted(set(verdicts.values()))}
    wrong = sorted(f"{m.rsplit('.', 2)[-2]}.{m.rsplit('.', 1)[-1]} WI-{i} ({v})" for (m, i), v in shown.items() if v in ("contradicted", "unrelated"))
    return {"labels": {"methods": len(methods), "areas": len(d["areas"]), "processes": len(d["processes"]), "rules": len(d["rules"])},
            "problems": problems, "areaBaseline": areas, "areaModel": model, "processes": processes, "rules": by_verdict, "intentOnDisagreeingCode": wrong}


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
    if "domain" in result:
        d = result["domain"]
        lines += ["", "## Domain labels (#187)", "",
                  f"{d['labels']['methods']} methods in {d['labels']['areas']} areas, {d['labels']['processes']} processes, "
                  f"{d['labels']['rules']} rules. Label problems: {len(d['problems'])}.", "",
                  "Baselines a domain model has to beat:", "",
                  f"- **Areas by namespace:** pair precision {d['areaBaseline']['precision']}, recall {d['areaBaseline']['recall']}"
                  + (f"; misplaced: {', '.join(d['areaBaseline']['misplaced'])}" if d['areaBaseline']['misplaced'] else ""),
                  f"- **Areas by `docwizz domain areas`:** pair precision {d['areaModel']['precision']}, recall {d['areaModel']['recall']}"
                  + (f"; misplaced: {', '.join(d['areaModel']['misplaced'])}" if d['areaModel']['misplaced'] else ""),
                  "- **Process steps reached by a crawl from the entry:** "
                  + ", ".join(f"{p['name']} {p['reached']}/{p['steps']}" for p in d["processes"]),
                  "- **Rules shown as As-intended, by verdict:** "
                  + ", ".join(f"{v} {x['asIntended']}/{x['labelled']}" for v, x in d["rules"].items()),
                  "- **As-intended on code that contradicts or isn't about the work item:** "
                  + (", ".join(d["intentOnDisagreeingCode"]) or "none")]
        lines += [f"- label problem: {p}" for p in d["problems"]]
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
            "domain": domain(dll, repos["indexed"], gold, work),
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
        problems += result["domain"]["problems"]
        for p in problems:
            print(f"FAIL: {p}", file=sys.stderr)
        return 1 if problems else 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
