"""Synthetic-only fixture contract and evaluation-boundary checks."""

from collections import Counter
from copy import deepcopy
from pathlib import Path
import unittest

from evaluation import _load
from synthetic_safety import model_state, validate_fixture


class SyntheticSafetyTests(unittest.TestCase):
    def setUp(self):
        folder = Path(__file__).parent / "synthetic-safety"
        self.inputs = _load(folder / "inputs.json")
        self.labels = _load(folder / "labels.json")

    def test_challenge_has_exact_separate_cohorts_and_all_abstention_reasons(self):
        validate_fixture(self.inputs, self.labels)
        self.assertEqual(Counter(row["outcome"] for row in self.labels),
                         {"abstain": 16, "broad_parent": 3, "assignable": 5})
        self.assertEqual(Counter(row["reason"] for row in self.labels if row["outcome"] == "abstain"),
                         {"nonpurchase": 4, "opaque": 3, "out_of_taxonomy": 3,
                          "cross_root": 3, "unclear_purpose": 3})

    def test_model_state_contains_only_one_description_and_taxonomy(self):
        for case in self.inputs["cases"]:
            state = model_state(self.inputs, case["id"])
            self.assertEqual(set(state), {"description", "taxonomy"})
            self.assertEqual(state["description"], case["description"])
            self.assertEqual(state["taxonomy"], self.inputs["taxonomy"])
        # Returned state must not mutate the stored fixture.
        state["taxonomy"][0]["name"] = "changed"
        self.assertNotEqual(self.inputs["taxonomy"][0]["name"], "changed")
        with self.assertRaises(ValueError):
            model_state(self.inputs, "unknown")

    def test_rejects_label_leakage_into_model_visible_input(self):
        for field in ("expected", "rationale", "outcome", "reason"):
            altered = deepcopy(self.inputs)
            altered["cases"][0][field] = "EVALUATOR_ONLY"
            with self.subTest(field=field), self.assertRaises(ValueError):
                validate_fixture(altered, self.labels)
            with self.assertRaises(ValueError):
                model_state(altered, altered["cases"][0]["id"])

    def test_rejects_invalid_hierarchies_duplicate_ids_and_label_misalignment(self):
        mutations = [
            lambda data: data["taxonomy"][0].update(parent="missing"),
            lambda data: data["taxonomy"][0].update(parent=data["taxonomy"][0]["id"]),
            lambda data: data["taxonomy"].append(deepcopy(data["taxonomy"][0])),
            lambda data: data["cases"].append(deepcopy(data["cases"][0])),
            lambda data: data["cases"][0].update(description=" "),
        ]
        for mutate in mutations:
            altered = deepcopy(self.inputs)
            mutate(altered)
            with self.assertRaises(ValueError):
                validate_fixture(altered, self.labels)
        for labels in (self.labels[:-1], self.labels + [self.labels[0]]):
            with self.assertRaises(ValueError):
                validate_fixture(self.inputs, labels)

    def test_rejects_impossible_expectations_and_wrong_denominators(self):
        edits = [(0, {"expected": "bread"}), (16, {"expected": "bread"}),
                 (19, {"expected": "food"}), (19, {"expected": "missing"}),
                 (0, {"reason": "assignable_control"}), (0, {"rationale": ""}),
                 (0, {"outcome": "assignable"})]
        for index, patch in edits:
            labels = deepcopy(self.labels)
            labels[index].update(patch)
            with self.subTest(patch=patch), self.assertRaises(ValueError):
                validate_fixture(self.inputs, labels)

    def test_clear_purchase_kind_controls_ignore_buyer_intent(self):
        by_id = {row["id"]: row for row in self.labels}
        for first, second, expected in (("s20", "s21", "bread"), ("s22", "s23", "mugs")):
            self.assertEqual(by_id[first]["expected"], expected)
            self.assertEqual(by_id[second]["expected"], expected)
            cases = {row["id"]: row for row in self.inputs["cases"]}
            self.assertEqual(cases[first]["group"], cases[second]["group"])


if __name__ == "__main__":
    unittest.main()
