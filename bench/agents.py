#!/usr/bin/env python3
"""Coding agents with and without docwizz: does docwizz context make a local agent measurably better?

    python3 bench/agents.py --agent reference                    # self-test: every check passes on the known solution
    python3 bench/agents.py --agent noop                         # self-test: no task passes when nothing is done
    python3 bench/agents.py --agent aider --model openai/qwen3-coder --endpoint http://127.0.0.1:8000/v1 --runs 3
    python3 bench/agents.py --agent command --cmd 'opencode run -m local/qwen3-coder "$(cat {prompt_file})"'
    python3 bench/run.py agents ...                              # the same, through the benchmark entry point

Each task (bench/agents/tasks.json) runs in a fresh git copy of fixture/ or fixture-legacy/, once per variant and run:

    none     the repository as it is
    docs     plus docs/ from `docwizz generate` and the AGENTS.md template (CLI.md, "Context for coding agents")
    context  plus the instruction to use `docwizz context` / the `docwizz mcp` server for facts about one target

A `docwizz` command is on the agent's PATH in the docs and context variants. Success is decided by the task's
executable checks (docwizz's own analysis of the result, or the answer in ANSWER.md), never by the agent's say-so.
Per run it records success, wall time, and what the agent adapter can report: steps (LLM calls), prompt and completion
tokens, files read. Results go to --out as JSON, and a Markdown report to stdout (and --report).

Not part of CI: a real run takes an hour or more on a local model. The self-tests take a few minutes and need no model.
"""
import argparse
import json
import os
import re
import shutil
import statistics
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
VARIANTS = ["none", "docs", "context"]

ANSWER_NOTE = "\n\nWrite your final answer to the file ANSWER.md in the repository root."
DOCS_NOTE = "Before you start, read AGENTS.md: it says where the generated documentation of this repository is.\n\n"
CONTEXT_NOTE = ("Before you start, read AGENTS.md. For facts about one symbol, file, folder or endpoint, run "
                "`docwizz context . --for \"<target>\"` (for example `--for \"POST /api/orders\"`) instead of exploring "
                "files one by one; if your harness supports MCP, the `docwizz mcp .` server answers the same questions.\n\n")


def sh(cmd, cwd, env=None, timeout=None, check=True):
    p = subprocess.run(cmd, cwd=cwd, env=env, capture_output=True, text=True, timeout=timeout)
    if check and p.returncode != 0:
        sys.exit(f"{cmd if isinstance(cmd, str) else ' '.join(cmd)} failed ({p.returncode}):\n{p.stderr[-2000:]}")
    return p


def build(dll=None):
    if dll:
        return dll
    sh(["dotnet", "build", os.path.join(ROOT, "src", "DocWizz"), "-c", "Release", "--nologo", "-v", "q"], ROOT)
    return sh(["dotnet", "msbuild", os.path.join(ROOT, "src", "DocWizz"), "-getProperty:TargetPath", "-p:Configuration=Release"], ROOT).stdout.strip()


def docwizz(dll, args, cwd):
    return sh(["dotnet", dll, *args], cwd, check=False)


def workspace(task, variant, dll, base):
    """A git copy of the task's repository, prepared for the variant; returns (dir, env, prompt)."""
    work = tempfile.mkdtemp(prefix=f"docwizz-agent-{task['id']}-", dir=base)
    repo = os.path.join(work, "repo")
    shutil.copytree(os.path.join(ROOT, task["repo"]), repo, ignore=shutil.ignore_patterns("node_modules", "docs", "bin", "obj"))
    tooldir = os.path.join(work, "bin")
    os.makedirs(tooldir)
    env = dict(os.environ)
    prompt = task["prompt"] + (ANSWER_NOTE if task["kind"] == "question" else "")
    if variant != "none":
        with open(os.path.join(tooldir, "docwizz"), "w") as f:
            f.write(f'#!/bin/sh\nexec dotnet "{dll}" "$@"\n')
        os.chmod(os.path.join(tooldir, "docwizz"), 0o755)
        env["PATH"] = tooldir + os.pathsep + env["PATH"]
        shutil.copy(os.path.join(ROOT, "templates", "AGENTS.md"), os.path.join(repo, "AGENTS.md"))
        prompt = (CONTEXT_NOTE if variant == "context" else DOCS_NOTE) + prompt
    git = ["git", "-c", "user.name=bench", "-c", "user.email=bench@example.com"]
    sh(["git", "init", "-q", "-b", "main"], repo)
    sh(git + ["add", "-A"], repo)
    sh(git + ["commit", "-q", "-m", "task"], repo)
    if variant != "none":   # generated after the commit: the agent's change is judged against the code, not the docs
        docwizz(dll, ["generate", "."], repo)
    env["DOCWIZZ_MCP_COMMAND"] = f'dotnet "{dll}" mcp {repo}'
    return work, repo, env, prompt


# --- agents: each returns a metrics dict (any of steps, prompt_tokens, completion_tokens, files_read) ---------------------

def agent_reference(task, repo, prompt, env, args):
    """Applies the task's known-good solution: proves the checks pass when the task is done."""
    ref = task["reference"]
    for c in ref.get("create", []):
        os.makedirs(os.path.dirname(os.path.join(repo, c["file"])), exist_ok=True)
        open(os.path.join(repo, c["file"]), "w").write(c["content"])
    for e in ref.get("edits", []):
        path = os.path.join(repo, e["file"])
        text = open(path).read()
        if e["find"] not in text:
            sys.exit(f"{task['id']}: reference edit does not apply to {e['file']}: {e['find'][:60]!r}")
        open(path, "w").write(text.replace(e["find"], e["replace"], 1))
    if "answer" in ref:
        open(os.path.join(repo, "ANSWER.md"), "w").write(ref["answer"] + "\n")
    return {"steps": 0}


def agent_noop(task, repo, prompt, env, args):
    """Does nothing: proves no task passes by accident."""
    return {"steps": 0}


def agent_aider(task, repo, prompt, env, args):
    """Aider, headless, against an OpenAI-compatible endpoint (--endpoint, key from OPENAI_API_KEY or 'local')."""
    prompt_file = os.path.join(repo, "..", "prompt.txt")
    open(prompt_file, "w").write(prompt)
    env = dict(env, OPENAI_API_BASE=args.endpoint or env.get("OPENAI_API_BASE", ""), OPENAI_API_KEY=env.get("OPENAI_API_KEY", "local"))
    cmd = ["aider", "--yes-always", "--no-auto-commits", "--no-show-model-warnings", "--no-check-update", "--no-pretty",
           "--model", args.model, "--temperature", str(args.temperature), "--message-file", prompt_file]
    p = subprocess.run(cmd, cwd=repo, env=env, capture_output=True, text=True, timeout=args.timeout)
    out = p.stdout + p.stderr
    def k(v):   # "2.3k" → 2300
        return int(float(v[:-1]) * 1000) if v.endswith("k") else int(float(v))
    tokens = re.findall(r"Tokens: ([\d.]+k?) sent, ([\d.]+k?) received", out)
    return {"steps": len(tokens), "prompt_tokens": sum(k(s) for s, _ in tokens), "completion_tokens": sum(k(r) for _, r in tokens),
            "files_read": len(set(re.findall(r"Added (\S+) to the chat", out)))}


def agent_command(task, repo, prompt, env, args):
    """Any harness: --cmd is a shell command run in the repository. {prompt_file} is the prompt, {metrics_file} a JSON file
    the command may write with steps, prompt_tokens, completion_tokens, files_read; {model} and {endpoint} are passed on."""
    prompt_file = os.path.join(repo, "..", "prompt.txt")
    metrics_file = os.path.join(repo, "..", "metrics.json")
    open(prompt_file, "w").write(prompt)
    cmd = args.cmd   # placeholders replaced literally, so the command may contain JSON braces of its own
    for k, v in {"prompt_file": prompt_file, "metrics_file": metrics_file, "model": args.model or "", "endpoint": args.endpoint or ""}.items():
        cmd = cmd.replace("{" + k + "}", v)
    subprocess.run(cmd, shell=True, cwd=repo, env=env, capture_output=True, text=True, timeout=args.timeout)
    try:
        return json.load(open(metrics_file))
    except (OSError, ValueError):
        return {}


AGENTS = {"reference": agent_reference, "noop": agent_noop, "aider": agent_aider, "command": agent_command}


# --- checks --------------------------------------------------------------------------------------------------------------

def analyze(dll, repo):
    p = docwizz(dll, ["analyze", ".", "--format", "json"], repo)
    return json.loads(p.stdout) if p.returncode == 0 else None


def run_checks(task, dll, repo):
    """Every check of the task, as (name, passed, detail)."""
    results = []
    report = None
    model = None
    for c in task["checks"]:
        t = c["type"]
        if t in ("documented", "no_violation") and report is None:
            report = analyze(dll, repo) or {}
        if t in ("endpoint", "http_caller") and model is None:
            path = os.path.join(repo, "..", "check-model.json")
            docwizz(dll, ["scan", ".", path], repo)
            model = json.load(open(path)) if os.path.exists(path) else {"nodes": [], "edges": []}
        if t == "documented":
            items = {i["id"]: i for i in report.get("documentation", {}).get("items", [])}
            bad = [i for i in c["ids"] if items.get(i, {}).get("status") != "documented"]
            results.append((t, not bad, f"not documented: {bad}" if bad else "ok"))
        elif t == "no_violation":
            hits = [v for v in report.get("architecture", {}).get("violations", []) if v["rule"] == c["rule"] and v["fromFile"] == c["from"]]
            results.append((t, report != {} and not hits, f"{len(hits)} {c['rule']} from {c['from']}" if hits else "ok"))
        elif t == "check_since":
            p = docwizz(dll, ["check", ".", "--since", "HEAD", "--format", "json"], repo)
            try:
                ok = json.loads(p.stdout)["check"]["pass"]
            except (ValueError, KeyError, TypeError):
                ok = False
            results.append((t, ok and p.returncode == 0, "ok" if ok else (p.stdout[-300:] or p.stderr[-300:])))
        elif t in ("endpoint", "http_caller"):
            nodes = {n["id"]: n for n in model["nodes"]}
            label = lambda n: f"{n['tags'][1]} /{(n.get('route') or '').lstrip('/')}" if "endpoint" in (n.get("tags") or []) and len(n["tags"]) > 1 else None
            ends = [n["id"] for n in model["nodes"] if label(n) == (c.get("label") or c.get("endpoint"))]
            if t == "endpoint":
                # Reached: the endpoint's own calls, one level of interface dispatch, and calls from there.
                impl = {e["to"]: e["from"] for e in model["edges"] if e["kind"] == "implements"}
                out = lambda i: [e["to"] for e in model["edges"] if e["from"] == i and e["kind"] in ("calls", "injects")]
                reached = {x for i in ends for x in out(i)}
                reached |= {impl[x] for x in reached if x in impl}
                ok = bool(ends) and any(c["reaches"] in x for x in reached)
                results.append((t, ok, "ok" if ok else f"endpoint {c['label']}: {'missing' if not ends else 'does not reach ' + c['reaches']}"))
            else:
                callers = [e["from"] for e in model["edges"] if e["kind"] == "http" and e["to"] in ends]
                ok = any(nodes.get(f, {}).get("file", "").startswith(c["under"]) for f in callers)
                results.append((t, ok, "ok" if ok else f"no caller of {c['endpoint']} under {c['under']}: {callers}"))
        elif t == "answer":
            path = os.path.join(repo, "ANSWER.md")
            answer = open(path).read().lower() if os.path.exists(path) else ""
            missing = [x for x in c["expect"] if x.lower() not in answer]
            wrong = [x for x in c["reject"] if x.lower() in answer]
            results.append((t, bool(answer) and not missing and not wrong,
                            "no ANSWER.md" if not answer else f"missing {missing}, wrong {wrong}" if missing or wrong else "ok"))
        else:
            sys.exit(f"{task['id']}: unknown check {t}")
    return results


# --- running and reporting -------------------------------------------------------------------------------------------------

def run(args):
    tasks = json.load(open(args.tasks))["tasks"]
    if args.only:
        tasks = [t for t in tasks if t["id"] in args.only.split(",")]
    dll = build(args.dll)
    base = tempfile.mkdtemp(prefix="docwizz-agents-")
    runs = []
    try:
        for task in tasks:
            for variant in args.variants.split(","):
                for i in range(args.runs):
                    work, repo, env, prompt = workspace(task, variant, dll, base)
                    start = time.perf_counter()
                    try:
                        metrics = AGENTS[args.agent](task, repo, prompt, env, args)
                        error = None
                    except subprocess.TimeoutExpired:
                        metrics, error = {}, f"timed out after {args.timeout} s"
                    seconds = round(time.perf_counter() - start, 2)
                    checks = run_checks(task, dll, repo)
                    changed = sh(["git", "status", "--porcelain"], repo).stdout.splitlines()
                    ok = all(p for _, p, _ in checks) and error is None
                    runs.append({"task": task["id"], "kind": task["kind"], "variant": variant, "run": i + 1, "success": ok,
                                 "seconds": seconds, "changed_files": len([c for c in changed if not c.endswith("ANSWER.md")]),
                                 "error": error, "checks": [{"type": t, "pass": p, "detail": d} for t, p, d in checks], **metrics})
                    print(f"  {task['id']:<22} {variant:<8} run {i + 1}: {'pass' if ok else 'FAIL'}  {seconds:6.1f}s"
                          + ("" if ok else f"  ({'; '.join(d for _, p, d in checks if not p) or error})"), file=sys.stderr)
                    if not args.keep:
                        shutil.rmtree(work, ignore_errors=True)
    finally:
        if not args.keep:
            shutil.rmtree(base, ignore_errors=True)
    return {"agent": args.agent, "model": args.model, "endpoint": args.endpoint, "temperature": args.temperature,
            "runs_per_variant": args.runs, "harness_version": harness_version(args), "environment": environment(), "runs": runs}


def harness_version(args):
    exe = {"aider": "aider"}.get(args.agent)
    if not exe or not shutil.which(exe):
        return None
    return subprocess.run([exe, "--version"], capture_output=True, text=True).stdout.strip() or None


def environment():
    dotnet = subprocess.run(["dotnet", "--version"], capture_output=True, text=True).stdout.strip()
    commit = subprocess.run(["git", "rev-parse", "--short", "HEAD"], cwd=ROOT, capture_output=True, text=True).stdout.strip()
    return {"docwizz_commit": commit or None, "dotnet": dotnet, "python": sys.version.split()[0], "date": time.strftime("%Y-%m-%d")}


def report(result):
    """Per variant: success rate, and mean ± standard deviation of time, steps and tokens over all runs."""
    def stat(values):
        values = [v for v in values if v is not None]
        if not values:
            return "—"
        mean = statistics.mean(values)
        return f"{mean:.1f}" + (f" ± {statistics.stdev(values):.1f}" if len(values) > 1 else "")
    lines = [f"Agent `{result['agent']}`" + (f", model `{result['model']}`" if result["model"] else "")
             + f", temperature {result['temperature']}, {result['runs_per_variant']} run(s) per task and variant.", "",
             "| Variant | Success | Seconds | Steps | Prompt tokens | Completion tokens | Files read |", "|---|---|---|---|---|---|---|"]
    for v in VARIANTS:
        rs = [r for r in result["runs"] if r["variant"] == v]
        if not rs:
            continue
        ok = sum(r["success"] for r in rs)
        lines.append(f"| {v} | {ok}/{len(rs)} ({100 * ok / len(rs):.0f}%) | {stat([r['seconds'] for r in rs])} | {stat([r.get('steps') for r in rs])} | "
                     f"{stat([r.get('prompt_tokens') for r in rs])} | {stat([r.get('completion_tokens') for r in rs])} | {stat([r.get('files_read') for r in rs])} |")
    lines += ["", "| Task | " + " | ".join(v for v in VARIANTS if any(r["variant"] == v for r in result["runs"])) + " |",
              "|---|" + "---|" * len({r["variant"] for r in result["runs"]})]
    for task in dict.fromkeys(r["task"] for r in result["runs"]):
        cells = []
        for v in VARIANTS:
            rs = [r for r in result["runs"] if r["task"] == task and r["variant"] == v]
            if rs:
                cells.append(f"{sum(r['success'] for r in rs)}/{len(rs)}")
        lines.append(f"| {task} | " + " | ".join(cells) + " |")
    return "\n".join(lines) + "\n"


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--agent", choices=sorted(AGENTS), required=True)
    ap.add_argument("--cmd", help="with --agent command: the shell command that runs the agent")
    ap.add_argument("--model", help="model name as the harness expects it")
    ap.add_argument("--endpoint", help="OpenAI-compatible endpoint of the local model, e.g. http://127.0.0.1:8000/v1")
    ap.add_argument("--temperature", type=float, default=0.0)
    ap.add_argument("--variants", default=",".join(VARIANTS))
    ap.add_argument("--runs", type=int, default=1, help="runs per task and variant (use 3+ for a report: results vary)")
    ap.add_argument("--only", help="comma-separated task ids")
    ap.add_argument("--timeout", type=int, default=1200, help="seconds per agent run")
    ap.add_argument("--tasks", default=os.path.join(HERE, "agents", "tasks.json"))
    ap.add_argument("--dll", help="a built DocWizz.dll (default: build src/DocWizz in Release)")
    ap.add_argument("--out", default=os.path.join(HERE, "agents-results.json"))
    ap.add_argument("--report", help="also write the Markdown report here")
    ap.add_argument("--keep", action="store_true", help="keep the workspaces (printed paths) for inspection")
    ap.add_argument("--expect", choices=["all", "none"], help="exit 1 unless all / no runs succeed (the self-tests)")
    args = ap.parse_args(argv)
    if args.agent == "command" and not args.cmd:
        ap.error("--agent command needs --cmd")
    if args.agent == "aider" and not args.model:
        ap.error("--agent aider needs --model")
    result = run(args)
    with open(args.out, "w") as f:
        json.dump(result, f, indent=2)
    md = report(result)
    print(md)
    if args.report:
        open(args.report, "w").write(md)
    passed = [r["success"] for r in result["runs"]]
    if args.expect == "all" and not all(passed) or args.expect == "none" and any(passed):
        sys.exit(f"expected {args.expect} runs to succeed: {sum(passed)}/{len(passed)} did")


if __name__ == "__main__":
    main()
