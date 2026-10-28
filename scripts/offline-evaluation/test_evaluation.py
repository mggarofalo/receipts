import json
import math
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from evaluation import group_split, score, validate_probabilities, wilson


class EvaluationTests(unittest.TestCase):
    def setUp(self):
        self.cases = [
            {"id": str(i), "group": f"group-{i // 2}", "expected": "alpha"}
            for i in range(8)
        ]

    def test_split_keeps_groups_together_and_is_order_independent(self):
        split = group_split(self.cases, 0.25, "seed")
        self.assertEqual(split, group_split(list(reversed(self.cases)), 0.25, "seed"))
        assignments = {row["id"]: row["split"] for row in split}
        self.assertEqual(sum(row["split"] == "holdout" for row in split), 2)
        for i in range(0, 8, 2):
            self.assertEqual(assignments[str(i)], assignments[str(i + 1)])

    def test_invalid_cohorts_and_split_sizes_are_rejected(self):
        for cases in ([], self.cases[:2], self.cases + [self.cases[0]]):
            with self.subTest(cases=cases), self.assertRaises(ValueError):
                group_split(cases, 0.25, "seed")
        for fraction in (0, 1, math.nan, True):
            with self.subTest(fraction=fraction), self.assertRaises(ValueError):
                group_split(self.cases, fraction, "seed")

    def test_scoring_preserves_abstention_failure_and_unattempted_denominators(self):
        predictions = [
            {"id": "0", "status": "answered", "label": "alpha"},
            {"id": "1", "status": "answered", "label": "beta"},
            {"id": "2", "status": "abstained"},
            {"id": "3", "status": "failed"},
        ]
        report = score(self.cases[:6], predictions)
        self.assertEqual(report["counts"], {"total": 6, "answered": 2, "correct": 1,
                                          "abstained": 1, "failed": 1, "unattempted": 2})
        self.assertAlmostEqual(report["accuracy"]["value"], 1 / 6)
        self.assertAlmostEqual(report["coverage"]["value"], 2 / 6)
        self.assertEqual(report["selective_accuracy"]["value"], 0.5)
        empty = score([], [])
        self.assertIsNone(empty["accuracy"]["value"])
        self.assertIsNone(score(self.cases, [])["selective_accuracy"]["interval95"])

    def test_malformed_or_misaligned_predictions_fail_closed(self):
        invalid = [
            [{"id": "unknown", "status": "failed"}],
            [{"id": "0", "status": "answered"}],
            [{"id": "0", "status": "abstained", "label": "alpha"}],
            [{"id": "0", "status": "success"}],
            [{"id": "0", "status": "failed"}] * 2,
        ]
        for predictions in invalid:
            with self.subTest(predictions=predictions), self.assertRaises(ValueError):
                score(self.cases, predictions)

    def test_probability_validation_rejects_invalid_values_and_candidate_drift(self):
        expected = ["alpha", "beta"]
        self.assertEqual(validate_probabilities({"alpha": 0.7, "beta": 0.3}, expected),
                         {"alpha": 0.7, "beta": 0.3})
        invalid = [None, {"alpha": 1}, {"alpha": 1, "gamma": 0},
                   {"alpha": True, "beta": 0}, {"alpha": math.nan, "beta": 0},
                   {"alpha": math.inf, "beta": 0}, {"alpha": -0.1, "beta": 1.1},
                   {"alpha": 0.3, "beta": 0.3}]
        for probabilities in invalid:
            with self.subTest(probabilities=probabilities), self.assertRaises(ValueError):
                validate_probabilities(probabilities, expected)

    def test_wilson_reference_and_zero_denominator(self):
        low, high = wilson(5, 10)
        self.assertAlmostEqual(low, 0.236593, places=5)
        self.assertAlmostEqual(high, 0.763407, places=5)
        self.assertIsNone(wilson(0, 0))

    def test_explicit_unattempted_and_invalid_case_fields(self):
        report = score(self.cases, [{"id": "0", "status": "unattempted"}])
        self.assertEqual(report["counts"]["unattempted"], 8)
        for field in ("id", "group", "expected"):
            case = dict(self.cases[0], **{field: " "})
            with self.subTest(field=field), self.assertRaises(ValueError):
                score([case], [])

    def test_cli_rejects_duplicate_keys_and_nonfinite_json_without_writing_output(self):
        script = str(Path(__file__).with_name("evaluation.py"))
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cases = root / "cases.json"
            predictions = root / "predictions.json"
            output = root / "report.json"
            predictions.write_text("[]", encoding="utf-8")
            for content in ('[{"id":"a","id":"b"}]', '[NaN]', '[Infinity]'):
                cases.write_text(content, encoding="utf-8")
                result = subprocess.run([sys.executable, script, "score", "--cases", str(cases),
                                         "--predictions", str(predictions), "--output", str(output)],
                                        capture_output=True, text=True)
                self.assertEqual(result.returncode, 2)
                self.assertFalse(output.exists())

    def test_cli_uses_explicit_files_and_refuses_to_overwrite(self):
        script = str(Path(__file__).with_name("evaluation.py"))
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cohort = root / "cases.json"
            output = root / "split.json"
            cohort.write_text(json.dumps(self.cases), encoding="utf-8")
            command = [sys.executable, script, "split", "--cases", str(cohort),
                       "--output", str(output), "--holdout-fraction", "0.25", "--seed", "x"]
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(len(json.loads(output.read_text(encoding="utf-8"))), 8)
            self.assertNotEqual(subprocess.run(command, capture_output=True).returncode, 0)
            predictions = root / "predictions.json"
            predictions.write_text("[]", encoding="utf-8")
            report = root / "report.json"
            result = subprocess.run([sys.executable, script, "score", "--cases", str(cohort),
                                     "--predictions", str(predictions), "--output", str(report)],
                                    capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(json.loads(report.read_text())["counts"]["unattempted"], 8)


if __name__ == "__main__":
    unittest.main()
