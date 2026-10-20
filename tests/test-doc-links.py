#!/usr/bin/env python3
"""Small self-contained regression tests for check-doc-links."""
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

CHECKER = Path(__file__).resolve().parents[1] / "scripts" / "check-doc-links.py"


class DocumentationLinkChecks(unittest.TestCase):
    def run_check(self, pages):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name, text in pages.items():
                destination = root / name
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_text(text, encoding="utf-8")
            return subprocess.run(
                [sys.executable, str(CHECKER), str(root)],
                capture_output=True, text=True, check=False,
            )

    def test_valid_document_with_canonical_paths(self):
        result = self.run_check({
            "docs/README.md": "# Index\n\n[Guide](guides/my-guide.md)\n",
            "docs/guides/my-guide.md": "# Guide\n\n[Back](../README.md)\n",
        })
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_missing_local_target_is_an_error(self):
        result = self.run_check({"docs/README.md": "# Index\n\n[Missing](guides/absent.md)\n"})
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("missing target", result.stderr)

    def test_noncanonical_name_and_escape_are_errors(self):
        result = self.run_check({
            "docs/README.md": "# Index\n\n[Escape](../../secrets.md)\n",
            "docs/Old_File.md": "# Improper filename\n",
        })
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("link escapes repository", result.stderr)
        self.assertIn("non-canonical filename", result.stderr)


    def test_legacy_redirects_and_numbered_adrs_are_errors(self):
        result = self.run_check({
            "docs/README.md": "# Index\n",
            "docs/guides/old-topic.md": "# Moved\n\n[New](../README.md)\n",
            "docs/adr/0001-old-decision.md": "# Old decision\n",
        })
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("obsolete compatibility redirect", result.stderr)
        self.assertIn("unsupported directory", result.stderr)
        self.assertIn("non-canonical filename", result.stderr)

    def test_code_fences_and_external_links_are_ignored(self):
        result = self.run_check({
            "docs/README.md": "# Index\n\n\x60\x60\x60md\n[Not a link](no.md)\n\x60\x60\x60\n\n"
                              "\x60[not a link](also-no.md)\x60 and [external](https://example.com/no.md)\n",
        })
        self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == "__main__":
    unittest.main()
