#!/usr/bin/env python3
"""Does `docwizz domain explain --ai` keep only what its evidence carries? Measured against a scripted model (#288).

    python3 bench/domain_explain.py             # Markdown report to stdout
    python3 bench/domain_explain.py --expect    # exit 1 unless no unsupported statement survives as a fact (test.sh)

The repository is the domain benchmark's (bench/domain_fixture.py, with its imported history). Per process in
bench/domain/gold.json, `domain explain` runs from the process entry against a fake model on 127.0.0.1. The fake model
reads the evidence it is sent and answers with labelled statements, one or more per category:

    supported      a relation's two symbols, citing that relation; a chain's first and last symbol, citing the chain
    wrong-evidence a relation's sentence citing another relation that doesn't touch its symbols
    unconnected    two symbols with no relation between them, citing only their symbol facts
    invented-code  a sentence naming code that isn't in the evidence
    invented-id    a sentence citing only ids that don't exist
    intent-only    a statement about code resting only on a historical requirement
    business       business meaning written as a technical fact
    claim          "implements the requirement" / "is approved"
    verb-mismatch  a relation's symbols with another verb ("deletes" for `calls`): not checked, measured as a known gap

Per category: kept as a technical fact, moved to the hypotheses, rejected. Per process: gold steps the deterministic
flow chains reach (the rest are missing steps), and steps a crawl reaches at all. A second run must give identical
output from the cache, without a request. No real model, no network beyond loopback.
"""
import argparse
import http.server
import json
import os
import shutil
import sys
import tempfile
import threading

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import domain_fixture as fixture  # noqa: E402
from requirements import build, digest, ratio, sh  # noqa: E402

GOLD = os.path.join(HERE, "domain", "gold.json")
SUPPORTED = "supported"
# Categories that must never come out as a technical fact; verb-mismatch is the documented gap and only reported.
UNSUPPORTED = ["wrong-evidence", "unconnected", "invented-code", "invented-id", "intent-only", "business", "claim"]
KNOWN_GAP = "verb-mismatch"


def name(symbol_id):
    """`cs:Shop.Orders.OrderService.Place(int, decimal)` -> `Shop.Orders.OrderService.Place`."""
    return symbol_id.split(":", 1)[-1].split("(")[0]


def statements(facts):
    """Labelled statements about the evidence `facts` (the `facts` list DocWizz sent): [(section, category, text, ids)]."""
    by_kind = {}
    for f in facts:
        by_kind.setdefault(f["kind"], []).append(f)
    names = {f["id"]: f["text"].split("`")[1] for f in by_kind.get("symbol", [])}
    # Relations between two different methods (`A` calls `B`), as their names.
    relations = [(f["id"], *[p for i, p in enumerate(f["text"].split("`")) if i % 2], f["text"].split("`")[2].strip())
                 for f in by_kind.get("relation", [])]
    relations = [(i, a, b, verb) for i, a, b, verb in relations if a != b and verb in ("calls", "persists", "publishes", "http")]
    out = []
    for i, a, b, verb in relations[:4]:
        out.append(("facts", SUPPORTED, f"`{a}` {verb} `{b}` in this flow.", [i]))
        out.append(("facts", KNOWN_GAP, f"`{a}` deletes `{b}`.", [i]))
    for c in by_kind.get("chain", [])[:2]:
        steps = [p for k, p in enumerate(c["text"].split("`")) if k % 2]
        out.append(("facts", SUPPORTED, f"The flow starts at `{steps[0]}` and reaches `{steps[-1]}`.", [c["id"]]))
    # Another relation sharing no symbol with the first: its sentence cites the wrong evidence.
    if relations:
        i, a, b, verb = relations[0]
        other = next((r for r in relations[1:] if not {r[1], r[2]} & {a, b}), None)
        if other:
            out.append(("facts", "wrong-evidence", f"`{a}` {verb} `{b}`, as the evidence shows.", [other[0]]))
    # Two symbols no relation connects.
    connected = {frozenset((a, b)) for _, a, b, _ in relations}
    linked = {frozenset(p) for f in by_kind.get("relation", []) for p in [[x for k, x in enumerate(f["text"].split("`")) if k % 2]]}
    methods = [(i, n) for i, n in names.items() if "." in n]
    pair = next(((x, y) for x in methods for y in methods if x[1] < y[1] and frozenset((x[1], y[1])) not in connected | linked), None)
    if pair:
        out.append(("facts", "unconnected", f"`{pair[0][1]}` calls `{pair[1][1]}`.", [pair[0][0], pair[1][0]]))
    if relations:
        i, a, _, _ = relations[0]
        out.append(("facts", "invented-code", f"`{a}` also writes to `AuditTrail.Append`.", [i]))
        out.append(("facts", "invented-id", f"`{a}` runs in a transaction.", ["R999", "X1"]))
        out.append(("facts", "business", f"`{a}` is the heart of the ordering business capability.", [i]))
    for w in by_kind.get("requirement", [])[:2]:
        target = w["text"].split("`")[1]
        out.append(("facts", "intent-only", f"`{target}` enforces what the work item asks.", [w["id"]]))
        out.append(("hypotheses", "claim", f"Possibly `{target}` implements the requirement {w['text'].split()[0]}.", [w["id"]]))
    if relations:
        out.append(("facts", "claim", "The checkout flow is approved for production.", [relations[0][0]]))
    return out


class Model(http.server.ThreadingHTTPServer):
    """A loopback Ollama that answers with labelled statements and remembers each statement's label by its text."""

    def __init__(self):
        self.labels, self.requests, self.lock = {}, 0, threading.Lock()
        super().__init__(("127.0.0.1", 0), Handler)

    @property
    def url(self):
        return f"http://127.0.0.1:{self.server_address[1]}"


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def do_POST(self):
        request = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))))
        facts = json.loads(request["messages"][1]["content"])["facts"]
        answer = {"facts": [], "hypotheses": [], "questions": []}
        with self.server.lock:
            self.server.requests += 1
            for section, category, text, ids in statements(facts):
                self.server.labels[text] = category
                answer[section].append({"text": text, "evidence": ids})
        data = json.dumps({"message": {"content": json.dumps(answer)}}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


def explain(dll, repo, entry, url, depth=4):
    env = dict(os.environ, OLLAMA_HOST=url, DOCWIZZ_MODEL="fake:1")
    env.pop("DOCWIZZ_DEBUG", None)
    p = sh(["dotnet", dll, "domain", "explain", ".", "--seed", entry, "--depth", str(depth), "--ai", "--format", "json"], repo, env=env, check=False)
    return p


def evaluate(dll, repo, gold):
    model = Model()
    threading.Thread(target=model.serve_forever, daemon=True).start()
    outcomes = {c: {"labelled": 0, "fact": 0, "hypothesis": 0, "rejected": 0} for c in [SUPPORTED, *UNSUPPORTED, KNOWN_GAP]}
    processes, reproducible, problems = [], True, []
    kept_facts, kept_hypotheses = set(), set()
    try:
        for p in gold["domain"]["processes"]:
            first = explain(dll, repo, p["entry"], model.url)
            before = model.requests
            again = explain(dll, repo, p["entry"], model.url)
            if (first.returncode, first.stdout) != (again.returncode, again.stdout) or model.requests != before:
                reproducible = False
            if first.returncode != 0:
                problems.append(f"{p['name']}: exit {first.returncode}: {first.stderr.strip()[:200]}")
                continue
            e = json.loads(first.stdout)
            kept_facts |= {s["text"] for s in e["facts"] if s["origin"] == "model"}
            kept_hypotheses |= {s["text"] for s in e["hypotheses"]}
            chains = {name(i) for f in e["evidence"]["facts"] if f["kind"] == "chain" for i in f["symbols"]}
            reached = {name(f["symbols"][0]) for f in e["evidence"]["facts"] if f["kind"] == "symbol"}
            steps = p["steps"]
            processes.append({"name": p["name"], "steps": len(steps), "inChains": len(set(steps) & chains),
                              "reached": len(set(steps) & reached), "missing": sorted(set(steps) - chains),
                              "chains": sum(f["kind"] == "chain" for f in e["evidence"]["facts"])})
        # Every labelled statement ends up a fact, a hypothesis, or neither (rejected).
        for text, category in model.labels.items():
            row = outcomes[category]
            row["labelled"] += 1
            row["fact" if text in kept_facts else "hypothesis" if text in kept_hypotheses else "rejected"] += 1
    finally:
        model.shutdown()
    leaked = {c: outcomes[c]["fact"] for c in UNSUPPORTED if outcomes[c]["fact"]}
    problems += [f"{c}: {n} unsupported statement(s) kept as technical facts" for c, n in leaked.items()]
    if outcomes[SUPPORTED]["labelled"] == 0:
        problems.append("the fake model wrote no supported statement: the evidence package changed shape")
    elif outcomes[SUPPORTED]["fact"] < outcomes[SUPPORTED]["labelled"]:
        problems.append(f"supported statements lost: {outcomes[SUPPORTED]['fact']} of {outcomes[SUPPORTED]['labelled']} kept")
    missing = [c for c in UNSUPPORTED if outcomes[c]["labelled"] == 0]
    problems += [f"no {c} statement was generated" for c in missing]
    if not reproducible:
        problems.append("a second run differed or asked the model again")
    unsupported = sum(outcomes[c]["labelled"] for c in UNSUPPORTED)
    return {
        "outcomes": outcomes,
        "supportedKept": ratio(outcomes[SUPPORTED]["fact"], outcomes[SUPPORTED]["labelled"]),
        "unsupportedAsFact": ratio(sum(outcomes[c]["fact"] for c in UNSUPPORTED), unsupported),
        "verbMismatchAsFact": ratio(outcomes[KNOWN_GAP]["fact"], outcomes[KNOWN_GAP]["labelled"]),
        "processes": processes,
        "reproducible": reproducible,
        "requests": model.requests,
        "problems": problems,
    }


def markdown(result):
    r = result["results"]
    lines = ["# Domain explanation benchmark", "",
             f"DocWizz {result['docwizz']}, gold `{result['inputs']['gold']}`, history `{result['inputs']['history']}`.", "",
             "| Metric | Value |", "|---|---|",
             f"| supported statements kept as technical facts | {r['supportedKept']} |",
             f"| unsupported statements kept as technical facts | {r['unsupportedAsFact']} |",
             f"| verb mismatches kept as technical facts (known gap) | {r['verbMismatchAsFact']} |",
             f"| reproducible from the cache, no second request | {r['reproducible']} |", "",
             "| Category | Labelled | Fact | Hypothesis | Rejected |", "|---|---|---|---|---|"]
    lines += [f"| {c} | {o['labelled']} | {o['fact']} | {o['hypothesis']} | {o['rejected']} |" for c, o in r["outcomes"].items()]
    lines += ["", "| Process | Gold steps | In flow chains | Reached by the crawl | Chains | Missing from the chains |", "|---|---|---|---|---|---|"]
    lines += [f"| {p['name']} | {p['steps']} | {p['inChains']} | {p['reached']} | {p['chains']} | {', '.join(m.rsplit('.', 2)[-2] + '.' + m.rsplit('.', 1)[-1] for m in p['missing']) or 'none'} |"
              for p in r["processes"]]
    lines += [f"- problem: {p}" for p in r["problems"]]
    return "\n".join(lines) + "\n"


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dll", help="a built DocWizz.dll (default: build Release)")
    ap.add_argument("--out", help="write the JSON result here")
    ap.add_argument("--expect", action="store_true", help="exit 1 if an unsupported statement survives as a fact")
    args = ap.parse_args(argv)

    dll = os.path.abspath(build(args.dll))
    gold = json.load(open(GOLD))
    work = tempfile.mkdtemp(prefix="docwizz-explainbench-")
    try:
        repo = os.path.join(work, "repo")
        history, items = fixture.build(repo, os.path.join(work, "exports"))
        sh(["dotnet", dll, "requirements", "import", ".", history, items], repo)
        result = {
            "schema": "docwizz.domain-explain-benchmark/v1",
            "docwizz": sh(["dotnet", dll, "--version"], work).stdout.strip(),
            "inputs": {"gold": digest(GOLD), "history": digest(history)},
            "results": evaluate(dll, repo, gold),
        }
    finally:
        shutil.rmtree(work, ignore_errors=True)
    if args.out:
        with open(args.out, "w") as f:
            json.dump(result, f, indent=2)
    print(markdown(result))
    if args.expect:
        for p in result["results"]["problems"]:
            print(f"FAIL: {p}", file=sys.stderr)
        return 1 if result["results"]["problems"] else 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
