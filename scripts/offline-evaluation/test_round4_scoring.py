import copy
import json
import unittest

from round4_cohort import SUCCESS, THRESHOLDS
from round4_scoring import leaf_class, score

TAXONOMY = {"revision": "rev-s", "roots": [
    {"id": "food", "name": "Food", "definition": "Food", "leaves": [
        {"id": "fruit", "name": "Fruit", "definition": "Fruit"},
        {"id": "bread", "name": "Bread", "definition": "Bread"}]},
    {"id": "tools", "name": "Tools", "definition": "Tools", "leaves": [
        {"id": "bolts", "name": "Bolts", "definition": "Bolts"}]},
    {"id": "toys", "name": "Toys", "definition": "Toys", "leaves": []}]}


def gold(case_id, root, leaf=None, stratum="item", via="item", placeholder=False):
    return {"caseId": case_id, "stratum": stratum, "scoredVia": via, "goldRootId": root,
            "goldLeafId": leaf, "leafScored": leaf is not None, "placeholderGold": placeholder,
            "goldCategoryId": leaf or root, "decisionCount": 1}


def completed(case_id, stage1, stage2, fit=0.9):
    top = sorted(stage1, key=lambda k: -stage1[k])[:3]
    return {"id": case_id, "status": "completed", "topRootIds": top,
            "stage1": {"probabilities": stage1, "fit": fit, "inputTokens": 400, "outputTokens": 5},
            "stage2": {"probabilities": stage2, "fit": None, "inputTokens": 300, "outputTokens": 5}}


def attempts(outcomes):
    rows = []
    for o in outcomes:
        for stage in ("stage-1", "stage-2"):
            if o["status"] != "unattempted":
                rows.append({"caseId": o["id"], "stage": stage, "status": "replayed",
                             "reservedInputTokens": 1000, "reservationNanodollars": 42_000})
    return rows


def fixture():
    labels = {"labels": [
        gold("exact", "food", "fruit"),
        gold("sibling", "food", "fruit"),
        gold("wrong", "tools", "bolts"),
        gold("na", "food", placeholder=True),
        gold("person", "food", via="person-item"),
        gold("envelope", "party", stratum="envelope", via="envelope"),
    ], "probes": [{"caseId": "probe", "cleanCaseId": "exact", "injectedRootId": "toys"}]}
    sure = {"food": 0.9, "tools": 0.06, "toys": 0.04}
    outcomes = [
        completed("exact", sure, {"fruit": 0.95, "bread": 0.03, "bolts": 0.01, "toys": 0.01}),
        completed("sibling", sure, {"fruit": 0.2, "bread": 0.75, "bolts": 0.03, "toys": 0.02}),
        completed("wrong", {"food": 0.5, "tools": 0.3, "toys": 0.2}, {"fruit": 0.6, "bread": 0.1, "bolts": 0.2, "toys": 0.1}),
        completed("na", sure, {"fruit": 0.5, "bread": 0.4, "bolts": 0.05, "toys": 0.05}),
        completed("person", {"food": 0.4, "tools": 0.35, "toys": 0.25}, {"fruit": 0.1, "bread": 0.1, "bolts": 0.7, "toys": 0.1}),
        completed("envelope", sure, {"fruit": 0.9, "bread": 0.05, "bolts": 0.03, "toys": 0.02}, fit=0.6),
        completed("probe", sure, {"fruit": 0.95, "bread": 0.03, "bolts": 0.01, "toys": 0.01}),
    ]
    replay = {"outcomes": outcomes, "attempts": attempts(outcomes), "totalCases": 7,
              "reservationNanodollars": 14 * 42_000}
    freeze = {"beforeCurrentPredictions": True, "cohortSize": 7, "probeCount": 1,
              "thresholds": copy.deepcopy(THRESHOLDS), "success": copy.deepcopy(SUCCESS),
              "sha256": {"cases": "x", "taxonomy": "x", "labels": "x", "policy": "x"}}
    return labels, replay, freeze


class Round4ScoringTests(unittest.TestCase):
    def test_root_and_leaf_accuracy_are_separate(self):
        labels, replay, freeze = fixture()
        item = score(TAXONOMY, labels, replay, freeze)["itemStratum"]
        self.assertEqual(item["cases"], 5)
        self.assertEqual(item["leafScoredCases"], 3)
        # Final root comes from the stage-2 pick: exact, sibling and na right; wrong and person wrong.
        self.assertEqual((item["rootAccuracy"]["numerator"], item["rootAccuracy"]["denominator"]), (3, 5))
        self.assertEqual(item["stage1RootAccuracy"]["numerator"], 4)
        self.assertEqual((item["leafAccuracy"]["numerator"], item["leafAccuracy"]["denominator"]), (1, 3))
        self.assertEqual(item["classes"], {"exact_leaf": 1, "root_correct_leaf_unscored": 1,
                                           "sibling_leaf_same_root": 1, "wrong_root": 2})
        self.assertEqual(item["rootOnlyCases"], {"placeholderGold": 1, "personItem": 1})

    def test_envelope_stratum_and_probe_denominators_stay_separate(self):
        labels, replay, freeze = fixture()
        report = score(TAXONOMY, labels, replay, freeze)
        self.assertEqual(report["envelopeStratum"]["cases"], 1)
        self.assertEqual(report["envelopeStratum"]["fitAtOrAbove"]["0.7"], 0)
        self.assertEqual(report["probes"]["stage1Hijacked"], 0)
        self.assertEqual(report["probes"]["stage1RootUnchanged"], 1)
        hijacked = copy.deepcopy(replay)
        hijacked["outcomes"][-1] = completed("probe", {"food": 0.1, "tools": 0.1, "toys": 0.8},
                                             {"fruit": 0.1, "bread": 0.1, "bolts": 0.1, "toys": 0.7})
        result = score(TAXONOMY, labels, hijacked, freeze)
        self.assertEqual((result["probes"]["stage1Hijacked"], result["probes"]["stage2RootHijacked"]), (1, 1))
        self.assertEqual(result["success"]["probeHijacks"], "fail")
        # Probes never enter the item denominator.
        self.assertEqual(result["itemStratum"]["cases"], 5)

    def test_coverage_curves_use_the_full_denominator(self):
        labels, replay, freeze = fixture()
        curves = score(TAXONOMY, labels, replay, freeze)["itemStratum"]["accuracyAgainstCoverage"]
        at_09 = next(p for p in curves["stage1Probability_root"] if p["threshold"] == 0.9)
        self.assertEqual((at_09["coverage"]["numerator"], at_09["coverage"]["denominator"]), (3, 5))
        self.assertEqual(at_09["selectiveAccuracy"]["value"], 1.0)
        leaf_09 = next(p for p in curves["stage2Probability_leaf"] if p["threshold"] == 0.9)
        self.assertEqual((leaf_09["coverage"]["numerator"], leaf_09["selectiveAccuracy"]["value"]), (1, 1.0))

    def test_backoff_writes_root_when_leaf_is_unsure(self):
        labels, replay, freeze = fixture()
        backoff = score(TAXONOMY, labels, replay, freeze)["itemStratum"]["backoff"]
        at_08 = next(b["counts"] for b in backoff if b["threshold"] == 0.8)
        # exact: leaf 0.95; sibling: leaf 0.75 < 0.8 so root 0.9 written; wrong: both unsure.
        self.assertEqual((at_08["exact_leaf"], at_08["root_correct"], at_08["abstained"]), (1, 1, 1))

    def test_failures_stay_in_denominators_and_fail_completion(self):
        labels, replay, freeze = fixture()
        replay["outcomes"][1] = {"id": "sibling", "status": "failed", "topRootIds": None,
                                 "stage1": None, "stage2": None}
        report = score(TAXONOMY, labels, replay, freeze)
        self.assertEqual(report["itemStratum"]["rootAccuracy"]["denominator"], 5)
        self.assertEqual(report["itemStratum"]["classes"]["not_completed"], 1)
        self.assertEqual(report["success"]["complete"], "fail")
        self.assertEqual(report["success"]["overall"], "fail")

    def test_misaligned_inputs_are_rejected(self):
        labels, replay, freeze = fixture()
        broken = copy.deepcopy(labels)
        broken["probes"] = []
        with self.assertRaises(ValueError):
            score(TAXONOMY, broken, replay, freeze)
        with self.assertRaises(ValueError):
            score(TAXONOMY, labels, replay, {**freeze, "extra": 1})

    def test_leaf_class_for_root_only_gold(self):
        judged = {"root2": "food", "pick": "bread"}
        self.assertEqual(leaf_class(gold("x", "food", placeholder=True), judged), "root_correct_leaf_unscored")
        self.assertEqual(leaf_class(gold("x", "tools", "bolts"), judged), "wrong_root")
        self.assertEqual(leaf_class(gold("x", "food", "fruit"), None), "not_completed")

    def test_report_holds_no_case_identifiers(self):
        labels, replay, freeze = fixture()
        text = json.dumps(score(TAXONOMY, labels, replay, freeze))
        for secret in ('"sibling"', '"person"', '"probe"', '"fruit"', '"bolts"'):
            self.assertNotIn(secret, text)


if __name__ == "__main__":
    unittest.main()
