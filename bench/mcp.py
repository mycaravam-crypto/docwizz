#!/usr/bin/env python3
"""Measure repeated MCP impact/check calls on deterministic medium/large fixtures.

Requires Python 3, git, .NET 10, Node and installed scanner-vue dependencies.
Run after PR #205 is merged: python3 bench/mcp.py --sizes medium,large
No baseline is inferred from scan timings: report actual first and repeated call times.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
SIZES = {"medium": 100, "large": 400}


def request(process, number, tool, arguments=None):
    message = {"jsonrpc": "2.0", "id": number, "method": "tools/call",
               "params": {"name": tool, "arguments": arguments or {}}}
    start = time.perf_counter()
    process.stdin.write(json.dumps(message) + "\n")
    process.stdin.flush()
    line = process.stdout.readline()
    seconds = time.perf_counter() - start
    if not line:
        raise RuntimeError("MCP terminated without a response: " + process.stderr.read()[-1000:])
    response = json.loads(line)
    if "error" in response or response.get("result", {}).get("isError"):
        raise RuntimeError(f"MCP {tool} failed: {line[:1000]}")
    return round(seconds, 3)


def benchmark(size, dll):
    with tempfile.TemporaryDirectory(prefix=f"docwizz-mcp-{size}-") as temp:
        root = Path(temp) / "repo"
        subprocess.run([sys.executable, str(HERE / "synth.py"), str(root),
                        str(SIZES[size])], check=True)
        subprocess.run(["git", "init", "-q", "-b", "main"], cwd=root, check=True)
        subprocess.run(["git", "add", "-A"], cwd=root, check=True)
        subprocess.run(["git", "-c", "user.name=bench", "-c",
                        "user.email=bench@example.com", "commit", "-qm", "baseline"],
                       cwd=root, check=True)
        # A real working-tree change ensures impact compares two distinct models.
        file = root / "backend" / "Application" / "Module0Service.cs"
        file.write_text(file.read_text() + "\n// benchmark change\n")
        env = os.environ.copy()
        env["DOCWIZZ_SCANNER_VUE"] = str(ROOT / "scanner-vue" / "index.mjs")
        with subprocess.Popen(["dotnet", str(dll), "mcp", str(root)],
                              stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                              stderr=subprocess.PIPE, text=True, bufsize=1,
                              env=env) as process:
            try:
                times = {
                    "impact_first": request(process, 1, "impact", {"budget": 100000}),
                    "impact_repeat": request(process, 2, "impact", {"budget": 100000}),
                    "check_first": request(process, 3, "check", {"budget": 100000}),
                    "check_repeat": request(process, 4, "check", {"budget": 100000}),
                }
            finally:
                process.stdin.close()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
            if process.returncode:
                raise RuntimeError(f"MCP exited {process.returncode}: {process.stderr.read()[-1000:]}")
        return times


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sizes", default="medium,large")
    parser.add_argument("--dll", type=Path, help="Prebuilt DocWizz.dll; otherwise build Release")
    args = parser.parse_args()
    sizes = args.sizes.split(",")
    if any(s not in SIZES for s in sizes):
        parser.error("sizes must be medium and/or large")
    if not (ROOT / "scanner-vue" / "node_modules").is_dir():
        parser.error("install scanner-vue dependencies first: npm ci --prefix scanner-vue")
    dll = args.dll
    if dll is None:
        dll = Path(tempfile.gettempdir()) / "docwizz-mcp-bench-build" / "DocWizz.dll"
        subprocess.run(["dotnet", "build", str(ROOT / "src" / "DocWizz"),
                        "-c", "Release", "-o", str(dll.parent), "--nologo", "-v", "q"], check=True)
    print("| Size | First impact | Repeat impact | First check | Repeat check |")
    print("|---|---:|---:|---:|---:|")
    for size in sizes:
        result = benchmark(size, dll.resolve())
        print("| " + size + " | " + " | ".join(f"{result[k]:.3f}s" for k in
              ("impact_first", "impact_repeat", "check_first", "check_repeat")) + " |")


if __name__ == "__main__":
    main()
