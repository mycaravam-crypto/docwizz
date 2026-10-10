#!/usr/bin/env python3
"""DocWizz benchmarks: scan, analyze and generate synthetic repositories of increasing size.

    python3 bench/run.py                         # small + medium, print a table, write bench/results.json
    python3 bench/run.py --sizes small,medium,large --compare bench/baseline.json
    python3 bench/run.py --sizes small --out /tmp/r.json
    python3 bench/run.py agents --agent reference      # coding agents with and without docwizz (bench/agents.py)

Per size and command it records wall time, peak memory (the whole process tree, Node scanner included), the stage
timings `--timings` prints, and the model's node/edge counts. `--compare` fails (exit 1) only on a *major* regression:
a command more than `--tolerance` times (default 2.0) slower or bigger than the baseline. Runner noise is large, so
this is a smoke alarm for order-of-magnitude problems, not a precise gate; it never runs in normal PR CI.
"""
import argparse
import json
import os
import platform
import re
import resource
import shutil
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
SIZES = {"small": 10, "medium": 100, "large": 400}   # modules; ~10 files each


def measure(cmd, cwd):
    """Runs cmd in a fresh child, returning (seconds, peak RSS MB of its process tree, stdout, stderr)."""
    probe = ("import resource,subprocess,sys,time;t=time.perf_counter();"
             "p=subprocess.run(sys.argv[1:],capture_output=True,text=True);"
             "sys.stdout.write(p.stdout);sys.stderr.write(p.stderr);"
             "r=resource.getrusage(resource.RUSAGE_CHILDREN).ru_maxrss;"
             "sys.stderr.write(f'\\nbench {time.perf_counter()-t} {r}\\n');sys.exit(p.returncode)")
    p = subprocess.run([sys.executable, "-c", probe, *cmd], cwd=cwd, capture_output=True, text=True)
    m = re.search(r"\nbench (\S+) (\d+)\n$", p.stderr)
    if p.returncode not in (0, 1) or not m:   # 1 = check-style failure, still a valid run
        sys.exit(f"{' '.join(cmd)} failed ({p.returncode}):\n{p.stderr[-2000:]}")
    rss_kb = int(m.group(2)) / (1024 if platform.system() == "Darwin" else 1)   # macOS reports bytes
    return float(m.group(1)), round(rss_kb / 1024), p.stdout, p.stderr


def stages(stderr):
    return {k: int(v) for k, v in re.findall(r"^timing (\S+) (\d+)$", stderr, re.M)}


def run(sizes, dll):
    results = {}
    for size in sizes:
        work = tempfile.mkdtemp(prefix=f"docwizz-bench-{size}-")
        try:
            repo = os.path.join(work, "repo")
            subprocess.run([sys.executable, os.path.join(HERE, "synth.py"), repo, str(SIZES[size])], check=True)
            files = sum(len(f) for _, _, f in os.walk(repo))
            entry = {"modules": SIZES[size], "files": files}
            dw = ["dotnet", dll]
            model = os.path.join(work, "model.json")
            for name, args in [("scan", ["scan", repo, model]), ("analyze", ["analyze", repo]),
                               ("generate", ["generate", repo, os.path.join(work, "docs")])]:
                secs, mb, out, err = measure([*dw, *args, "--timings"], work)
                entry[name] = {"seconds": round(secs, 2), "peak_mb": mb, "stages_ms": stages(err)}
                print(f"  {size:<7} {name:<9} {secs:7.2f}s {mb:6d} MB", file=sys.stderr)
            m = json.load(open(model))
            entry["nodes"], entry["edges"] = len(m["nodes"]), len(m["edges"])
            results[size] = entry
        finally:
            shutil.rmtree(work, ignore_errors=True)
    return results


def environment():
    dotnet = subprocess.run(["dotnet", "--version"], capture_output=True, text=True).stdout.strip()
    node = subprocess.run(["node", "--version"], capture_output=True, text=True).stdout.strip() if shutil.which("node") else None
    cpu = next((l.split(":", 1)[1].strip() for l in open("/proc/cpuinfo") if l.startswith("model name")), None) if os.path.exists("/proc/cpuinfo") else platform.processor()
    return {"os": platform.platform(), "cpu": cpu, "cpus": os.cpu_count(), "dotnet": dotnet, "node": node, "python": platform.python_version()}


def table(results):
    lines = ["| Size | Modules | Files | Nodes | Edges | scan | analyze | generate | Peak MB (generate) |", "|---|---|---|---|---|---|---|---|---|"]
    for size, r in results.items():
        lines.append(f"| {size} | {r['modules']} | {r['files']} | {r['nodes']} | {r['edges']} | {r['scan']['seconds']}s | "
                     f"{r['analyze']['seconds']}s | {r['generate']['seconds']}s | {r['generate']['peak_mb']} |")
    lines += ["", "Stages of `generate` (ms):", "", "| Size | " + " | ".join(k for k in next(iter(results.values()))["generate"]["stages_ms"] if k != "peak-memory-mb") + " |"]
    keys = [k for k in next(iter(results.values()))["generate"]["stages_ms"] if k != "peak-memory-mb"]
    lines.append("|---|" + "---|" * len(keys))
    for size, r in results.items():
        lines.append(f"| {size} | " + " | ".join(str(r["generate"]["stages_ms"].get(k, "")) for k in keys) + " |")
    return "\n".join(lines)


def compare(results, baseline, tolerance):
    problems = []
    for size, r in results.items():
        b = baseline["results"].get(size)
        if not b:
            continue
        for cmd in ("scan", "analyze", "generate"):
            for metric in ("seconds", "peak_mb"):
                now, then = r[cmd][metric], b[cmd][metric]
                if then > 0 and now > then * tolerance:
                    problems.append(f"{size} {cmd} {metric}: {now} vs baseline {then} (> {tolerance}x)")
        if b.get("nodes") and abs(r["nodes"] - b["nodes"]) > 0.25 * b["nodes"]:
            problems.append(f"{size} model size changed: {r['nodes']} nodes vs baseline {b['nodes']} — regenerate the baseline if intended")
    return problems


def main():
    if sys.argv[1:2] == ["agents"]:   # the agent benchmark has its own options: bench/agents.py
        sys.path.insert(0, HERE)
        import agents
        return agents.main(sys.argv[2:])
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--sizes", default="small,medium")
    ap.add_argument("--out", default=os.path.join(HERE, "results.json"))
    ap.add_argument("--compare", help="baseline JSON to check against")
    ap.add_argument("--tolerance", type=float, default=2.0)
    a = ap.parse_args()
    sizes = [s for s in a.sizes.split(",") if s]
    if unknown := [s for s in sizes if s not in SIZES]:
        sys.exit(f"unknown size {unknown}; known: {', '.join(SIZES)}")

    build = os.path.join(tempfile.gettempdir(), "docwizz-bench-build")
    subprocess.run(["dotnet", "build", os.path.join(ROOT, "src", "DocWizz"), "-c", "Release", "-o", build, "--nologo", "-v", "q"], check=True)
    subprocess.run(["npm", "ci", "--prefix", os.path.join(ROOT, "scanner-vue"), "--silent"], check=True)
    os.environ["DOCWIZZ_SCANNER_VUE"] = os.path.join(ROOT, "scanner-vue", "index.mjs")

    results = run(sizes, os.path.join(build, "DocWizz.dll"))
    report = {"environment": environment(), "date": time.strftime("%Y-%m-%d"), "results": results}
    with open(a.out, "w") as f:
        json.dump(report, f, indent=2)
    print(table(results))
    if a.compare:
        problems = compare(results, json.load(open(a.compare)), a.tolerance)
        print("\n" + ("Regressions:\n" + "\n".join(f"- {p}" for p in problems) if problems else f"No major regression against {a.compare}."))
        sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
