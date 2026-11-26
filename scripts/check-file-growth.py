"""Reject growth beyond per-file budgets; compare committed Git trees."""
import argparse
import subprocess
from pathlib import PurePosixPath

DOC = {".md", ".mdx", ".rst", ".adoc", ".txt"}
SOURCE = {".cs", ".java", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx",
          ".vue", ".py", ".sh", ".ps1", ".go", ".rs", ".css", ".scss",
          ".html", ".sql", ".yaml", ".yml"}

def budget(path):
    p = PurePosixPath(path)
    if p.parts[0] in {"fixture", "fixture-legacy"}:
        return None  # intentionally synthetic test inputs
    if p.suffix.lower() in DOC:
        return 400, 24000
    if p.suffix.lower() in SOURCE:
        return 600, 40000
    return None

def metrics(data):
    return len(data.splitlines()), len(data)

def violations(old, new, limits):
    return [(unit, before, after, limit)
            for unit, before, after, limit in zip(
                ("lines", "bytes"), metrics(old), metrics(new), limits)
            if after > max(before, limit)]

def git(*args):
    return subprocess.check_output(["git", *args])

def blob(ref, path):
    return git("show", f"{ref}:{path}")

def check(base, head):
    changes = git("diff", "--name-status", "-z", "--find-renames",
                  "--diff-filter=AMRT", base, head).decode(
                      "utf-8", errors="surrogateescape").split("\0")
    entries = iter(changes[:-1])
    failed = False
    for status in entries:
        previous = next(entries)
        path = next(entries) if status.startswith("R") else previous
        limits = budget(path)
        if limits is None:
            continue
        # Renames retain debt only within the same budget category.
        old = (blob(base, previous) if status[0] in "MR"
               and budget(previous) == limits else b"")
        new = blob(head, path)
        for unit, before, after, limit in violations(old, new, limits):
            print(f"{path!r}: {unit} {before} -> {after}; "
                  f"allowed {max(before, limit)} (budget {limit}). "
                  "Consolidate existing content or extract a cohesive module.")
            failed = True
    if not failed:
        print("File growth gate passed.")
    return int(failed)

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("base", help="Base Git commit/ref")
    parser.add_argument("head", nargs="?", default="HEAD")
    args = parser.parse_args()
    raise SystemExit(check(args.base, args.head))
