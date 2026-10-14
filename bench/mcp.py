#!/usr/bin/env python3
"""Measure repeated MCP impact/check calls on deterministic medium/large fixtures.

Requires Python 3, git, .NET 10, Node and installed scanner-vue dependencies: python3 bench/mcp.py --sizes medium,large
Startup (process start + model load, up to the initialize answer) is timed apart from the tool calls. Each tool's first
call is followed by --repeats calls in the same session; the median of those is reported, with each call's model source
so a repeat that rescanned shows instead of passing for a cache hit. No baseline is inferred from scan timings.
"""
import argparse
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys
import tempfile
import time

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
SIZES = {"medium": 100, "large": 400}
BUDGET = {"budget": 100000}


class Session:
    """One `docwizz mcp` process; stderr goes to a file so a chatty server can never block on a full pipe."""

    def __init__(self, dll, root, log, env):
        self.log = log
        self.next_id = 0
        self.process = subprocess.Popen(["dotnet", str(dll), "mcp", str(root)], stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=log, text=True, bufsize=1, env=env)

    def stderr(self):
        self.log.flush()
        return Path(self.log.name).read_text(errors="replace")[-1000:]

    def send(self, message):
        self.process.stdin.write(json.dumps(message) + "\n")
        self.process.stdin.flush()

    def call(self, method, params=None):
        """Sends one request; returns (seconds until its answer, result)."""
        self.next_id += 1
        start = time.perf_counter()
        self.send({"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params or {}})
        line = self.process.stdout.readline()
        seconds = time.perf_counter() - start
        if not line:
            self.process.wait(timeout=15)
            raise RuntimeError(f"MCP exited {self.process.returncode} without answering {method}: {self.stderr()}")
        response = json.loads(line)
        if response.get("id") != self.next_id:
            raise RuntimeError(f"MCP answered id {response.get('id')} to request {self.next_id}: {line[:1000]}")
        if "error" in response or response.get("result", {}).get("isError"):
            raise RuntimeError(f"MCP {method} {params} failed: {line[:1000]}")
        return seconds, response["result"]

    def tool(self, name, arguments):
        seconds, result = self.call("tools/call", {"name": name, "arguments": arguments})
        model = result.get("structuredContent", {}).get("model", {})
        return seconds, model.get("source", "?"), bool(model.get("stale"))

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        if self.process.returncode:
            raise RuntimeError(f"MCP exited {self.process.returncode}: {self.stderr()}")


def fixture(root, modules):
    subprocess.run([sys.executable, str(HERE / "synth.py"), str(root), str(modules)], check=True)
    git = ["git", "-c", "user.name=bench", "-c", "user.email=bench@example.com"]
    subprocess.run(git + ["init", "-q", "-b", "main"], cwd=root, check=True)
    subprocess.run(git + ["add", "-A"], cwd=root, check=True)
    subprocess.run(git + ["commit", "-qm", "baseline"], cwd=root, check=True)
    # A real working-tree change ensures impact compares two distinct models.
    file = root / "backend" / "Application" / "Module0Service.cs"
    file.write_text(file.read_text() + "\n// benchmark change\n")


def benchmark(size, dll, repeats):
    with tempfile.TemporaryDirectory(prefix=f"docwizz-mcp-{size}-") as temp:
        root = Path(temp) / "repo"
        fixture(root, SIZES[size])
        env = os.environ.copy()
        env["DOCWIZZ_SCANNER_VUE"] = str(ROOT / "scanner-vue" / "index.mjs")
        with open(Path(temp) / "stderr.log", "w+") as log:
            session = Session(dll, root, log, env)
            try:
                # The server loads its model before reading stdin: the initialize answer marks the end of startup.
                startup, _ = session.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                                         "clientInfo": {"name": "docwizz-bench", "version": "1"}})
                session.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
                result = {"startup": startup, "sources": []}
                for tool in ("impact", "check"):
                    calls = [session.tool(tool, BUDGET) for _ in range(1 + repeats)]
                    if any(stale for _, _, stale in calls):
                        raise RuntimeError(f"{size} {tool}: model was stale; timings cannot represent cache reuse")
                    if len({source for _, source, _ in calls}) != 1:
                        raise RuntimeError(f"{size} {tool}: model source changed during measurements: {calls}")
                    result[tool + "_first"] = calls[0][0]
                    result[tool + "_repeat"] = statistics.median(s for s, _, _ in calls[1:])
                    seen = dict.fromkeys(src + (" (stale)" if stale else "") for _, src, stale in calls)
                    result["sources"].append(f"{tool}: {' / '.join(seen)}")
            finally:
                session.close()
        return result


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--sizes", default="medium,large")
    parser.add_argument("--repeats", type=int, default=5, help="repeat calls per tool; the median is reported")
    parser.add_argument("--dll", type=Path, help="Prebuilt DocWizz.dll; otherwise build Release into a fresh temp dir")
    args = parser.parse_args()
    sizes = args.sizes.split(",")
    if any(s not in SIZES for s in sizes):
        parser.error("sizes must be medium and/or large")
    if args.repeats < 1:
        parser.error("--repeats must be at least 1")
    if not (ROOT / "scanner-vue" / "node_modules").is_dir():
        parser.error("install scanner-vue dependencies first: npm ci --prefix scanner-vue")
    with tempfile.TemporaryDirectory(prefix="docwizz-mcp-bench-build-") as build:
        dll = args.dll
        if dll is None:
            # A fresh directory per run: never time a stale build left behind by another checkout.
            dll = Path(build) / "DocWizz.dll"
            subprocess.run(["dotnet", "build", str(ROOT / "src" / "DocWizz"),
                            "-c", "Release", "-o", build, "--nologo", "-v", "q"], check=True)
        print(f"Repeat columns: median of {args.repeats} calls in the same session.\n")
        print("| Size | Startup | First impact | Repeat impact | First check | Repeat check | Model source |")
        print("|---|---:|---:|---:|---:|---:|---|")
        for size in sizes:
            r = benchmark(size, dll.resolve(), args.repeats)
            cells = [f"{r[k]:.3f}s" for k in ("startup", "impact_first", "impact_repeat", "check_first", "check_repeat")]
            print(f"| {size} | " + " | ".join(cells) + " | " + ", ".join(r["sources"]) + " |", flush=True)


if __name__ == "__main__":
    main()
