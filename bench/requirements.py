#!/usr/bin/env python3
"""Does historical business context make local-LLM documentation better? Measured, not assumed (#168).

    python3 bench/requirements.py                                   # links only: no model needed (also in test.sh)
    python3 bench/requirements.py --model fake                      # + the drafting pipeline against a fake local model
    python3 bench/requirements.py --model qwen2.5-coder:7b --endpoint http://127.0.0.1:11434 --modes A,B,C
    python3 bench/run.py requirements ...                           # the same, through the benchmark entry point

A repository with a scripted history is built from bench/requirements_fixture.py (24 C# methods, 20 commits, direct
and PR-only links, a squash merge, a rename, an ambiguous PR, a denied lookup, an unrelated co-change, two methods that
contradict their requirement, a prompt-injection work item, two methods over the default context bound), with the exports DocWizz reads. Ground truth is
bench/requirements/gold.json, written by hand; nothing a model wrote ever goes there.

Layer 1, links (deterministic): `docwizz requirements import` and `show` per method against gold. Link precision and
recall, separately for confirmed and candidate, outcome accuracy, and byte-identical output on a second import.

Layer 2, drafts (only with --model): `docwizz generate --ai` per mode, at temperature 0 (DocWizz's default):
    A  no business context (today's prompt)
    B  --requirements with the default budget (3 items, 1200 characters)
    C  --requirements, unbounded (10 items, 20000 characters): how much the bound matters
Every request goes through a recording proxy on 127.0.0.1 (with --model fake, the proxy is the model). Per mode:
grounded constraint recall (a gold phrase in the draft), unsupported work-item citations, contradictions flagged as
deviation, injection phrases kept, kept sentences, prompt and completion tokens, latency, and requests on an unchanged
second run (cache). 95% intervals by bootstrap over methods (seeded). JSON to --out, Markdown to stdout and --report.

Nothing is sent anywhere but the endpoint you name (which DocWizz itself restricts to loopback/private addresses), and
the proxy records every host a request went to.
"""
import argparse
import hashlib
import http.client
import http.server
import json
import os
import random
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import urllib.parse

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
import requirements_fixture as fixture  # noqa: E402

GOLD = os.path.join(HERE, "requirements", "gold.json")
MODES = {
    "A": {"flags": [], "config": None},
    "B": {"flags": ["--requirements"], "config": None},
    "C": {"flags": ["--requirements"], "config": {"max": 10, "budget": 20000}},
}


def sh(cmd, cwd, env=None, check=True):
    p = subprocess.run(cmd, cwd=cwd, env=env, capture_output=True, text=True)
    if check and p.returncode != 0:
        sys.exit(f"{' '.join(cmd)} failed ({p.returncode}):\n{p.stdout[-1500:]}\n{p.stderr[-1500:]}")
    return p


def build(dll):
    if dll:
        return dll
    sh(["dotnet", "build", os.path.join(ROOT, "src", "DocWizz"), "-c", "Release", "--nologo", "-v", "q"], ROOT)
    return sh(["dotnet", "msbuild", os.path.join(ROOT, "src", "DocWizz"), "-getProperty:TargetPath", "-p:Configuration=Release"], ROOT).stdout.strip()


def digest(path):
    h = hashlib.sha256()
    for f in sorted(os.path.join(d, n) for d, _, ns in os.walk(path) for n in ns) if os.path.isdir(path) else [path]:
        with open(f, "rb") as fh:
            h.update(os.path.relpath(f, path).encode() if os.path.isdir(path) else b"")
            h.update(fh.read())
    return h.hexdigest()[:16]


# ---- layer 1: links ----

def links(dll, repo, history, items, gold):
    dw = lambda *a: sh(["dotnet", dll, *a], repo)
    dw("requirements", "import", ".", history, items)
    index = os.path.join(repo, "docs", ".docwizz", "requirements.json")
    first = open(index, "rb").read()
    os.remove(index)
    dw("requirements", "import", ".", history, items)
    reproducible = first == open(index, "rb").read()

    rows, predicted, true = [], set(), set()
    for name, g in gold["methods"].items():
        answer = json.loads(dw("requirements", "show", ".", "--for", name, "--format", "json").stdout)
        got = {(r["id"], r["status"]) for r in answer["requirements"]}
        predicted |= {(name, i, s) for i, s in got}
        true |= {(name, l["id"], l["status"]) for l in g["links"]}
        rows.append({"method": name, "outcome": answer["outcome"], "expected": g["outcome"], "got": sorted(got),
                     "gold": sorted((l["id"], l["status"]) for l in g["links"])})
    for sid, g in gold["historical"].items():
        answer = json.loads(dw("requirements", "show", ".", "--for", sid, "--format", "json").stdout)
        rows.append({"method": sid, "outcome": answer["outcome"], "expected": g["outcome"],
                     "got": sorted((r["id"], r["status"]) for r in answer["requirements"]),
                     "gold": sorted((l["id"], l["status"]) for l in g["links"])})
    file_level = [wi for wi in gold["fileLevelOnly"]
                  if json.loads(dw("requirements", "show", ".", "--work-item", str(wi), "--format", "json").stdout)["symbols"]]

    pairs = lambda s: {(m, i) for m, i, _ in s}
    tp = pairs(predicted) & pairs(true)
    visible = {(m, i) for m, i, s in true if s != "none"}

    def by(status):
        p = {(m, i) for m, i, s in predicted if s == status}
        t = {(m, i) for m, i, s in true if s == status}
        return {"predicted": len(p), "gold": len(t), "precision": ratio(len(p & pairs(true)), len(p)), "recall": ratio(len(p & t), len(t))}

    return {
        "methods": len(gold["methods"]),
        "precision": ratio(len(tp), len(pairs(predicted))),
        "recall": ratio(len(tp), len(pairs(true))),
        "recallVisible": ratio(len(tp & visible), len(visible)),
        "statusAccuracy": ratio(sum(1 for m, i, s in predicted if (m, i, s) in true), len(tp)),
        "confirmed": by("confirmed"), "candidate": by("candidate"),
        "outcomeAccuracy": ratio(sum(r["outcome"] == r["expected"] for r in rows), len(rows)),
        "falsePositives": sorted(f"{m} WI-{i}" for m, i in pairs(predicted) - pairs(true)),
        "missed": sorted(f"{m} WI-{i}" for m, i in pairs(true) - pairs(predicted)),
        "wrongOutcomes": [f"{r['method']}: {r['outcome']} (expected {r['expected']})" for r in rows if r["outcome"] != r["expected"]],
        "fileLevelAsserted": file_level,
        "reproducible": reproducible,
        "indexFingerprint": json.load(open(index))["fingerprint"],
        "rows": rows,
    }


def ratio(a, b):
    return round(a / b, 3) if b else None


# ---- layer 2: drafts ----

class Recorder(http.server.ThreadingHTTPServer):
    """A loopback proxy that records every request, or the fake model itself when upstream is None."""

    def __init__(self, upstream):
        self.upstream = upstream
        self.log, self.lock = [], threading.Lock()
        super().__init__(("127.0.0.1", 0), Handler)

    @property
    def url(self):
        return f"http://127.0.0.1:{self.server_address[1]}"


class Handler(http.server.BaseHTTPRequestHandler):
    # HTTP/1.0 closes each connection after its answer, and a client reusing it then sees "response ended prematurely".
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def do_GET(self):
        self.respond(200, {"models": [{"name": "fake"}]})

    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        start = time.monotonic()
        up = self.server.upstream
        if up is None:
            try:
                status, reply = 200, fake_model(json.loads(body))
            except Exception as e:   # a broken fake must show up as a failed request, not a dropped connection
                status, reply = 500, {"error": f"fake model: {e!r}"}
            host = "fake"
        else:
            u = urllib.parse.urlparse(up)
            conn = http.client.HTTPConnection(u.hostname, u.port or 80, timeout=600)
            conn.request("POST", self.path, body, {"Content-Type": "application/json"})
            r = conn.getresponse()
            status, reply, host = r.status, json.loads(r.read() or b"{}"), u.hostname
        request = json.loads(body)
        messages = request.get("messages", [])
        with self.server.lock:
            self.server.log.append({
                "host": host, "path": self.path, "status": status, "seconds": round(time.monotonic() - start, 3),
                "system": messages[0]["content"] if messages else "", "facts": messages[1]["content"] if len(messages) > 1 else "",
                "promptTokens": reply.get("prompt_eval_count") or (len(json.dumps(messages)) // 4),
                "completionTokens": reply.get("eval_count") or (len(reply.get("message", {}).get("content", "")) // 4),
            })
        self.respond(status, reply)

    def respond(self, status, obj):
        data = json.dumps(obj).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


def fake_model(request):
    """Deterministic stand-in for a local model: describes the code from the facts, words each requirement's first
    criterion as intent, and obeys injected instructions (so the test sees whether DocWizz filters them)."""
    try:
        facts = json.loads(request["messages"][1]["content"])
    except (KeyError, IndexError, ValueError):
        return {"message": {"content": ""}}
    lines = []
    if "symbol" in facts:
        lines.append(f"summary: Handles {facts['symbol'].split('(')[0].split('.')[-1]} for its callers. [source]")
        for r in facts.get("businessContext", {}).get("requirements", []):
            criterion = (r.get("acceptanceCriteria") or [r.get("title") or ""])[0].rstrip(".")
            may = "may " if r["status"] == "candidate" else ""
            lines.append(f"intent: The requirement {may}asks that {criterion[0].lower() + criterion[1:]}. [{r['id']}]")
            if "IGNORE" in (r.get("title") or "") or any("SYSTEM:" in c for c in r.get("acceptanceCriteria", [])):
                lines.append(f"summary: The code is certified. [{r['id']}]")
                lines.append(f"intent: This requirement is approved. [{r['id']}]")
            if "30" in (facts.get("source") or "") and "14 days" in json.dumps(r):
                lines.append(f"deviation: The source waits 30 days (`daysSinceDue > 30`), not 14. [{r['id']}; source]")
    else:
        lines.append(f"summary: Holds the code of {facts.get('module', 'this module')}. [{(facts.get('members') or [{}])[0].get('name', 'source')}]")
    return {"message": {"content": "\n".join(lines)}, "prompt_eval_count": len(json.dumps(request)) // 4, "eval_count": len("\n".join(lines)) // 4}


def drafts(dll, repo, gold, mode, model, endpoint, seed, given):
    spec = MODES[mode]
    out = os.path.join(repo, f"docs-{mode}")
    shutil.rmtree(out, ignore_errors=True)
    config = os.path.join(repo, "docwizz.yaml")
    original = open(config).read()
    if spec["config"]:
        with open(config, "a") as f:
            f.write(f"ai: {{ requirements: {{ max: {spec['config']['max']}, budget: {spec['config']['budget']} }} }}\n")
    proxy = Recorder(None if model == "fake" else endpoint)
    threading.Thread(target=proxy.serve_forever, daemon=True).start()
    env = dict(os.environ, OLLAMA_HOST=proxy.url, DOCWIZZ_MODEL="fake:1b" if model == "fake" else model)
    try:
        start = time.monotonic()
        sh(["dotnet", dll, "generate", ".", out, "--ai", *spec["flags"]], repo, env)
        seconds = time.monotonic() - start
        first = list(proxy.log)
        sh(["dotnet", dll, "generate", ".", out, "--ai", *spec["flags"]], repo, env)
        second = len(proxy.log) - len(first)
    finally:
        proxy.shutdown()
        with open(config, "w") as f:
            f.write(original)

    cache = json.load(open(os.path.join(out, ".docwizz", "ai-cache.json")))
    bound = {}   # method → what the bound took away from its business context, as the facts said
    for r in first:
        try:
            facts = json.loads(r["facts"])
        except ValueError:
            continue
        bc = facts.get("businessContext") if isinstance(facts, dict) else None
        if bc and (bc.get("leftOut") or bc.get("criteriaCut")):
            bound[facts["symbol"].removeprefix("cs:").split("(")[0]] = {"leftOut": bc.get("leftOut") or 0, "criteriaCut": bool(bc.get("criteriaCut"))}
    per = {}
    for name, g in gold["methods"].items():
        prefix = "cs:" + name + "("
        entries = [v for k, v in sorted(cache.items()) if k.startswith(prefix) and ("~req:" in k) == (mode != "A")]
        sentences = [(sec, s["text"], s["from"]) for d in entries for sec, l in (d.get("sections") or {}).items() for s in l]
        text = " ".join(t for _, t, _ in sentences).lower()
        cited = [(sec, t) for sec, t, _ in sentences if re.search(r"\[WI-\d+", t)]
        ids = [set(map(int, re.findall(r"WI-(\d+)", t))) for _, t in cited]
        unsupported = [i for i in ids if not i <= {l["id"] for l in g["links"]}]   # not true for this method (gold)
        invalid = [i for i in ids if not i <= given.get(name, set())]               # not in what docwizz gave the model
        constraints = g.get("constraints", [])
        per[name] = {
            "sentences": len(sentences),
            "constraintsFound": sum(any(p.lower() in text for p in alts) for alts in constraints),
            "constraints": len(constraints),
            "wiSentences": len(cited), "unsupported": len(unsupported), "invalidCitations": len(invalid),
            "contradiction": bool(g.get("contradicts")), "deviation": any(sec == "deviation" for sec, _, _ in sentences),
            "injection": sum(p in text for p in gold["injection"]["phrases"]),
            "stress": bool(g.get("budgetStress")), "bound": bound.get(name),
            # Kept intent/deviation sentences, so unsupported claims and false deviations can be read, not only counted.
            "requirementSentences": [f"{sec}: {t}" for sec, t, _ in sentences if sec in ("intent", "deviation")],
        }
    rng = random.Random(seed)
    names = sorted(per)

    def ci(metric):
        values = []
        for _ in range(1000):
            sample = [per[rng.choice(names)] for _ in names]
            values.append(metric(sample))
        values = sorted(v for v in values if v is not None)
        return [round(values[int(0.025 * len(values))], 3), round(values[int(0.975 * len(values)) - 1], 3)] if values else None

    recall = lambda ms: ratio(sum(m["constraintsFound"] for m in ms), sum(m["constraints"] for m in ms))
    unsupported = lambda ms: ratio(sum(m["unsupported"] for m in ms), sum(m["wiSentences"] for m in ms))
    contradictions = [m for m in per.values() if m["contradiction"]]
    return {
        "mode": mode, "flags": spec["flags"], "config": spec["config"],
        "requests": len(first), "requestsOnUnchangedRerun": second,
        "hosts": sorted({r["host"] for r in proxy.log}),
        "promptFingerprints": sorted({hashlib.sha256(r["system"].encode()).hexdigest()[:12] for r in first}),
        "promptTokens": sum(r["promptTokens"] for r in first), "completionTokens": sum(r["completionTokens"] for r in first),
        "requestSeconds": round(sum(r["seconds"] for r in first), 2), "wallSeconds": round(seconds, 2),
        "businessContextRequests": sum('"businessContext"' in r["facts"] for r in first),
        "constraintRecall": recall(list(per.values())), "constraintRecallCI": ci(recall),
        "stressRecall": recall([m for m in per.values() if m["stress"]]),
        "boundHit": sorted(bound),
        "unsupportedRate": unsupported(list(per.values())), "unsupportedRateCI": ci(unsupported),
        "contradictionsFlagged": f"{sum(m['deviation'] for m in contradictions)}/{len(contradictions)}",
        "falseDeviations": sorted(n for n, m in per.items() if m["deviation"] and not m["contradiction"]),
        "injectionPhrasesKept": sum(m["injection"] for m in per.values()),
        "invalidCitations": sum(m["invalidCitations"] for m in per.values()),
        "sentences": sum(m["sentences"] for m in per.values()),
        "methods": per,
    }


def model_digest(endpoint, model):
    """The model build as the server names it (Ollama /api/tags), so a report says exactly which weights answered."""
    try:
        u = urllib.parse.urlparse(endpoint)
        conn = http.client.HTTPConnection(u.hostname, u.port or 80, timeout=10)
        conn.request("GET", "/api/tags")
        for m in json.loads(conn.getresponse().read()).get("models", []):
            if m.get("name") == model:
                return {"digest": m.get("digest"), **{k: m.get("details", {}).get(k) for k in ("parameter_size", "quantization_level", "format")}}
    except (OSError, ValueError):
        pass
    return None


# ---- report ----

def recommend(result):
    l = result["links"]
    lines = [f"Links: precision {l['precision']}, recall {l['recall']} ({l['recallVisible']} of what the evidence shows); "
             f"candidate precision {l['candidate']['precision']}."]
    modes = {m["mode"]: m for m in result.get("drafts", [])}
    if result["model"]["name"] in (None, "fake"):
        lines.append("No real model was measured: keep --requirements opt-in. Run with --model <local model> before changing the default.")
        return lines
    a, b = modes.get("A"), modes.get("B")
    if not (a and b):
        return lines + ["Modes A and B are both needed for a recommendation."]
    better = (b["constraintRecall"] or 0) > (a["constraintRecall"] or 0) and (b["constraintRecallCI"] or [0])[0] > (a["constraintRecall"] or 0)
    safe = (b["unsupportedRate"] or 0) <= 0.1 and b["injectionPhrasesKept"] == 0
    cost = b["promptTokens"] / max(1, a["promptTokens"])
    if better and safe and cost < 2 and l["precision"] >= 0.9:
        lines.append(f"Enable by default is defensible: constraint recall {a['constraintRecall']} → {b['constraintRecall']} (CI {b['constraintRecallCI']}), "
                     f"unsupported {b['unsupportedRate']}, prompt tokens ×{cost:.2f}.")
    elif better and safe:
        lines.append(f"Keep opt-in: better grounding ({a['constraintRecall']} → {b['constraintRecall']}) but prompt tokens ×{cost:.2f} or link precision {l['precision']}.")
    elif safe:
        lines.append(f"Keep opt-in: no significant gain (recall {a['constraintRecall']} → {b['constraintRecall']}, B's 95% CI {b['constraintRecallCI']} "
                     f"does not clear A), though safe (unsupported {b['unsupportedRate']}, injection phrases kept 0).")
    else:
        lines.append(f"Revise before wider use: recall {a['constraintRecall']} → {b['constraintRecall']}, unsupported {b['unsupportedRate']}, "
                     f"injection phrases kept {b['injectionPhrasesKept']}.")
    return lines


def markdown(result):
    l = result["links"]
    out = [f"# Requirement context benchmark", "",
           f"{result['docwizz']} · model {result['model']['name'] or '(none)'} ({result['model']['provider']}"
           f"{', ' + result['model']['digest']['digest'][:12] + ' ' + str(result['model']['digest']['quantization_level']) if result['model'].get('digest') else ''}) · seed {result['seed']} · "
           f"fixture {result['inputs']['history']} / work items {result['inputs']['workItems']} · index {l['indexFingerprint']}", "",
           "## Links (deterministic)", "",
           "| Methods | Precision | Recall | Recall (visible evidence) | Confirmed P/R | Candidate P/R | Outcomes | Reproducible |",
           "|---|---|---|---|---|---|---|---|",
           f"| {l['methods']} | {l['precision']} | {l['recall']} | {l['recallVisible']} | {l['confirmed']['precision']}/{l['confirmed']['recall']} | "
           f"{l['candidate']['precision']}/{l['candidate']['recall']} | {l['outcomeAccuracy']} | {l['reproducible']} |", ""]
    if l["falsePositives"]:
        out.append(f"- False positives: {', '.join(l['falsePositives'])}")
    if l["missed"]:
        out.append(f"- Missed: {', '.join(l['missed'])}")
    if l["wrongOutcomes"]:
        out.append(f"- Wrong outcomes: {', '.join(l['wrongOutcomes'])}")
    out.append(f"- Work items asserted on a method from a file-level change: {l['fileLevelAsserted'] or 'none'}")
    if result.get("drafts"):
        out += ["", "## Drafts", "",
                "| Mode | Requests | Rerun requests | Prompt tokens | Completion tokens | Seconds | Constraint recall (95% CI) | Stress recall | Bound hit | Unsupported WI (95% CI) | Invalid citations | Contradictions flagged | False deviations | Injection kept | Hosts |",
                "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
        for m in result["drafts"]:
            out.append(f"| {m['mode']} | {m['requests']} | {m['requestsOnUnchangedRerun']} | {m['promptTokens']} | {m['completionTokens']} | {m['requestSeconds']} | "
                       f"{m['constraintRecall']} {m['constraintRecallCI']} | {m['stressRecall']} | {len(m['boundHit'])} | {m['unsupportedRate']} {m['unsupportedRateCI']} | {m['invalidCitations']} | {m['contradictionsFlagged']} | {len(m['falseDeviations'])} | "
                       f"{m['injectionPhrasesKept']} | {', '.join(m['hosts'])} |")
        modes = result["drafts"]
        out += ["", "### Per method", "", "Constraints found / gold groups; `u` unsupported WI sentences, `d` a deviation sentence, `✂` the bound cut its context.", "",
                "| Method | " + " | ".join(m["mode"] for m in modes) + " |", "|---|" + "---|" * len(modes)]
        for name in sorted(modes[0]["methods"]):
            cells = []
            for m in modes:
                r = m["methods"][name]
                cells.append(f"{r['constraintsFound']}/{r['constraints']}" + (f" u{r['unsupported']}" if r["unsupported"] else "")
                             + (" d" if r["deviation"] else "") + (" ✂" if r["bound"] else ""))
            out.append(f"| {name.split('.', 1)[1]} | " + " | ".join(cells) + " |")
    out += ["", "## Recommendation", "", *[f"- {r}" for r in result["recommendation"]], ""]
    return "\n".join(out)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--model", help="model for layer 2: `fake`, or a local model name (Ollama /api/chat)")
    ap.add_argument("--endpoint", default="http://127.0.0.1:11434", help="the model server (only with a real --model)")
    ap.add_argument("--modes", default="A,B")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--dll", help="a built DocWizz.dll (default: build Release)")
    ap.add_argument("--out", help="write the JSON result here")
    ap.add_argument("--report", help="write the Markdown report here")
    ap.add_argument("--expect", action="store_true", help="exit 1 unless the links match gold's expected outcomes and, with --model fake, the pipeline rules hold")
    args = ap.parse_args(argv)

    dll = os.path.abspath(build(args.dll))
    gold = json.load(open(GOLD))
    work = tempfile.mkdtemp(prefix="docwizz-reqbench-")
    try:
        repo = os.path.join(work, "repo")
        history, items = fixture.build(repo, os.path.join(work, "exports"))
        result = {
            "schema": "docwizz.requirement-benchmark/v1",
            "docwizz": sh(["dotnet", dll, "--version"], work).stdout.strip(),
            "model": {"name": args.model, "provider": "ollama" if args.model else None, "endpoint": None if args.model in (None, "fake") else args.endpoint, "temperature": 0},
            "seed": args.seed,
            "inputs": {"history": digest(history), "workItems": digest(items), "gold": digest(GOLD), "budget": {"B": "3 items / 1200 chars", "C": "10 items / 20000 chars"}},
            "links": links(dll, repo, history, items, gold),
        }
        if args.model and args.model != "fake":
            result["model"]["digest"] = model_digest(args.endpoint, args.model)
        if args.model:
            given = {r["method"]: {i for i, _ in r["got"]} for r in result["links"]["rows"]}
            result["drafts"] = [drafts(dll, repo, gold, m.strip(), args.model, args.endpoint, args.seed, given) for m in args.modes.split(",")]
        result["recommendation"] = recommend(result)
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
        problems = expectations(result)
        for p in problems:
            print(f"FAIL: {p}", file=sys.stderr)
        return 1 if problems else 0
    return 0


def expectations(result):
    """What CI holds the pipeline to: the designed link outcomes, and with the fake model the drafting rules."""
    l, problems = result["links"], []
    if l["wrongOutcomes"]:
        problems.append(f"outcomes differ from gold: {l['wrongOutcomes']}")
    if not l["reproducible"]:
        problems.append("a rebuilt index has different bytes")
    if l["fileLevelAsserted"]:
        problems.append(f"file-level changes asserted on methods: {l['fileLevelAsserted']}")
    if l["candidate"]["predicted"] != l["candidate"]["gold"] or l["statusAccuracy"] != 1.0:
        problems.append(f"candidate/confirmed statuses differ from gold: {l['candidate']}, status accuracy {l['statusAccuracy']}")
    for m in result.get("drafts", []):
        if m["hosts"] != ["fake"] and result["model"]["name"] == "fake":
            problems.append(f"mode {m['mode']}: requests went to {m['hosts']}")
        if m["requestsOnUnchangedRerun"] != 0:
            problems.append(f"mode {m['mode']}: {m['requestsOnUnchangedRerun']} requests on an unchanged rerun (cache)")
        if m["mode"] == "A" and m["businessContextRequests"]:
            problems.append("mode A sent business context")
        if m["mode"] != "A" and result["model"]["name"] == "fake":
            if m["injectionPhrasesKept"]:
                problems.append(f"mode {m['mode']}: {m['injectionPhrasesKept']} injection phrases kept")
            if m["invalidCitations"]:
                problems.append(f"mode {m['mode']}: {m['invalidCitations']} citations of work items docwizz didn't give the model")
            if not m["businessContextRequests"]:
                problems.append(f"mode {m['mode']}: no request had business context")
    return problems


if __name__ == "__main__":
    sys.exit(main())
