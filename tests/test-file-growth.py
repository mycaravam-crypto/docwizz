"""Boundary and regression tests for the file growth gate."""
import runpy
import unittest
from pathlib import Path

gate = runpy.run_path(str(Path(__file__).resolve().parents[1] /
                          "scripts/check-file-growth.py"))
violations = gate["violations"]
budget = gate["budget"]

class GrowthTests(unittest.TestCase):
    def test_exact_limit(self):
        self.assertEqual([], violations(b"", b"x\n"*4, (4, 8)))

    def test_new_oversized(self):
        self.assertEqual(2, len(violations(b"", b"x\n"*5, (4, 8))))

    def test_legacy_growth(self):
        self.assertTrue(violations(b"x\n"*5, b"x\n"*6, (4, 8)))

    def test_legacy_reduction_and_equal_size(self):
        for size in (5, 6):
            self.assertEqual([], violations(b"x\n"*6, b"x\n"*size, (4, 8)))

    def test_long_line(self):
        self.assertEqual("bytes", violations(b"", b"x"*9, (4, 8))[0][0])

    def test_unterminated_line(self):
        self.assertEqual((1, 1), gate["metrics"](b"x"))

    def test_scope(self):
        self.assertEqual((400, 24000), budget("docs/guide.md"))
        self.assertEqual((600, 40000), budget("src/Main.cs"))
        self.assertIsNone(budget("fixture/guide.md"))
        self.assertIsNone(budget("scanner-vue/package-lock.json"))

if __name__ == "__main__":
    unittest.main()
