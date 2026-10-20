#!/usr/bin/env python3
"""Check links in maintained Markdown docs and canonical documentation filenames.

Only repository-local links are checked: external URLs and GitHub anchor slugs
are not verified offline. Documentation has no legacy redirect files.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path
from urllib.parse import unquote, urlsplit

LINK = re.compile(r"!?\[[^\]]*\]\((<?[^)\s>]+>?)(?:\s+[\"'][^\"']+[\"'])?\)")
FENCE = re.compile(r"^\s{0,3}(" + chr(96) + r"{3,}|~{3,})")
INLINE_CODE = re.compile(r"\x60+[^\x60]*\x60+")
KEBAB = re.compile(r"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*\.md$")
DIRECTORIES = {"guides", "reference", "internals", "archive"}
ROOT_DOCS = {"README.md", "cli.md", "getting-started.md"}


def lint(root: Path) -> list[str]:
    root = root.resolve()
    docs = root / "docs"
    if not docs.is_dir():
        return ["docs/ directory is missing"]
    files = sorted(docs.rglob("*.md"))
    if (root / "README.md").is_file():
        files.insert(0, root / "README.md")
    problems: list[str] = []
    for file in files:
        relative = file.relative_to(root).as_posix()
        content = file.read_text(encoding="utf-8")
        if file != root / "README.md":
            parts = file.relative_to(docs).parts
            if len(parts) == 1:
                if parts[0] not in ROOT_DOCS:
                    problems.append(f"{relative}: misplaced documentation: move into guides/reference/internals/archive")
            elif parts[0] not in DIRECTORIES:
                problems.append(f"{relative}: unsupported directory: {parts[0]}")
            if file.name != "README.md" and not KEBAB.fullmatch(file.name):
                problems.append(f"{relative}: non-canonical filename (use lowercase kebab-case.md)")
            if any(not re.fullmatch(r"[a-z][a-z0-9]*(?:-[a-z0-9]+)*", part)
                   for part in parts[:-1]):
                problems.append(f"{relative}: non-canonical directory name")
            if content.startswith("# Moved\n"):
                problems.append(f"{relative}: obsolete compatibility redirect; remove the file and update links")
        fence_char = ""
        fence_count = 0
        for index, source in enumerate(content.splitlines(), start=1):
            match = FENCE.match(source)
            if match:
                run = match.group(1)
                if not fence_char:
                    fence_char, fence_count = run[0], len(run)
                elif run[0] == fence_char and len(run) >= fence_count:
                    fence_char = ""
                continue
            if fence_char:
                continue
            line = INLINE_CODE.sub("", source)
            for link in LINK.finditer(line):
                raw = link.group(1).strip("<>")
                url = urlsplit(raw)
                if url.scheme or url.netloc or raw.startswith("#"):
                    continue
                if not url.path:
                    continue
                path = unquote(url.path)
                if path.startswith("/"):
                    problems.append(f"{relative}:{index}: absolute local link: {raw}")
                    continue
                target = (file.parent / path).resolve()
                if not target.is_relative_to(root):
                    problems.append(f"{relative}:{index}: link escapes repository: {raw}")
                elif not target.exists():
                    problems.append(f"{relative}:{index}: missing target: {raw}")
    return problems


def main() -> int:
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent
    problems = lint(root)
    if problems:
        print("\n".join(problems), file=sys.stderr)
        return 1
    print("Markdown local links and canonical documentation filenames: OK")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
