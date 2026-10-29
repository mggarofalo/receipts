"""Synthetic accounting checks only; never reads private pilot artifacts."""

from copy import deepcopy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from aggregate_scoring import aggregate


def fixture():
    taxonomy = [{"id": k, "parentId": p, "name": k, "definition": "Synthetic " + k} for k, p in
                (("root", None), ("parent", "root"), ("leaf", "parent"), ("other", None))]
    cases = [{"id": str(i), "request": {"categories": taxonomy,
              "taxonomyRevision": "synthetic", "description": "SYNTHETIC SECRET"}} for i in range(24)]
    labels = {"labels": [{"caseId": c["id"], "status": "labelable",
                         "maximallySpecificAcceptablePaths": [{"categoryId": "leaf",
                                                               "pathIds": ["root", "parent", "leaf"]}],
                         "otherSupportedCoarseAlternatives": [],
                         "validBroadAncestorIds": ["parent"],
                         "initialPassDisagreement": False, "uncertainty": None} for c in cases]}
    replay = {"totalCases": 24, "stopped": False, "attempts": [],
              "outcomes": [{"id": c["id"], "status": "completed",
                            "baseline": answer(), "treatment": answer()} for c in cases]}
    account(replay)
    return cases, labels, replay


def account(replay):
    """Synthetic hashes exercise structure; only production replay binds state."""
    attempts = []
    for row in replay["outcomes"]:
        if row["status"] == "unattempted":
            continue
        stages = ["baseline"]
        if row["baseline"]["failure"] is None and row["baseline"]["suggestion"]["categoryId"] is not None:
            stages.append("selected-path")
        for stage in stages:
            payload = json.dumps({"synthetic": row["id"], "stage": stage})
            attempts.append({"caseId": row["id"], "stage": stage, "requestJson": payload,
                             "requestSha256": hashlib.sha256(payload.encode()).hexdigest(),
                             "status": "unknown" if row["status"] == "unknown" and stage == stages[-1] else "replayed"})
    replay.update(attempts=attempts,
                  completedCases=sum(r["status"] == "completed" for r in replay["outcomes"]),
                  unattemptedCases=sum(r["status"] == "unattempted" for r in replay["outcomes"]),
                  reservationNanodollars=len(attempts) * 2_752_512,
                  priorCarryNanodollars=4_750_000_000,
                  combinedReservationNanodollars=len(attempts) * 2_752_512 + 4_750_000_000)


def answer(key="leaf"):
    paths = {"leaf": ["root", "parent", "leaf"], "parent": ["root", "parent"],
             "root": ["root"], "other": ["other"], None: []}
    return {"failure": None, "suggestion": {"categoryId": key, "path": paths[key],
                                            "abstentionReason": "no-fit" if key is None else None}}


class AggregateScoringTests(unittest.TestCase):
    def test_specific_broad_wrong_and_uncredited_ancestor(self):
        cases, labels, replay = fixture()
        for i, key in enumerate(("leaf", "parent", "other", "root")):
            replay["outcomes"][i]["baseline"] = answer(key)
            replay["outcomes"][i]["treatment"] = answer(key)
        result = aggregate(cases, labels, replay)
        counts = result["arms"]["treatment"]["counts"]
        self.assertEqual([counts[k] for k in ("most_specific", "broad", "wrong_branch",
                                              "other_unsupported", "accepted")], [21, 1, 1, 1, 22])
        self.assertEqual(result["arms"]["treatment"]["useful_coverage"]["denominator"], 24)
        self.assertEqual(sum(result["paired_transitions"].values()), 24)
        self.assertNotIn("SYNTHETIC SECRET", json.dumps(result))
        self.assertNotIn("leaf", json.dumps(result))

    def test_harmful_specificity_and_no_answer(self):
        cases, labels, replay = fixture()
        labels["labels"][0]["maximallySpecificAcceptablePaths"] = [
            {"categoryId": "parent", "pathIds": ["root", "parent"]}]
        labels["labels"][0]["validBroadAncestorIds"] = []
        label = labels["labels"][1]
        label.update(status="abstain_required", maximallySpecificAcceptablePaths=[], validBroadAncestorIds=[])
        replay["outcomes"][1]["treatment"] = answer(None)
        result = aggregate(cases, labels, replay)
        self.assertEqual(result["strata"]["broad_only"], 1)
        self.assertEqual(result["arms"]["treatment"]["counts"]["harmful_specificity"], 1)
        self.assertEqual(result["arms"]["baseline"]["counts"]["writes_on_no_answer"], 1)
        self.assertEqual(result["arms"]["treatment"]["counts"]["correct_no_answer_abstention"], 1)

    def test_selected_failure_and_unknown_keep_full_denominator(self):
        for status in ("failed", "unknown"):
            cases, labels, replay = fixture()
            replay["stopped"] = True
            replay["outcomes"][0].update(status=status, treatment={"failure": "Timeout", "suggestion": None})
            for row in replay["outcomes"][1:]:
                row.update(status="unattempted", baseline=None, treatment=None)
            account(replay)
            report = aggregate(cases, labels, replay)
            self.assertEqual(report["arms"]["treatment"]["counts"][status], 1)
            self.assertEqual(report["arms"]["treatment"]["counts"]["unattempted"], 23)
            self.assertEqual(report["arms"]["treatment"]["counts"]["abstained"], 0)
            self.assertEqual(report["arms"]["baseline"]["useful_coverage"]["value"], 1 / 24)
            self.assertIsNone(report["arms"]["treatment"]["selective_accuracy"]["value"])

    def test_unresolved_and_disagreement_sensitivity(self):
        cases, labels, replay = fixture()
        labels["labels"][0].update(status="unresolved", maximallySpecificAcceptablePaths=[],
                                     validBroadAncestorIds=[], initialPassDisagreement=True)
        labels["labels"][1].update(initialPassDisagreement=True, uncertainty="synthetic doubt")
        result = aggregate(cases, labels, replay)
        self.assertEqual(result["strata"]["unresolved"], 1)
        self.assertEqual(result["arms"]["baseline"]["counts"]["unresolved_assignment"], 1)
        self.assertEqual(result["arms"]["baseline"]["agreement_only_useful_coverage"]["numerator"], 22)
        self.assertEqual(result["arms"]["baseline"]["unambiguous_useful_coverage"]["numerator"], 22)

    def test_alignment_invalid_paths_and_short_cohort_fail_closed(self):
        for mutate in (
            lambda c, l, r: c.pop(),
            lambda c, l, r: r["outcomes"].pop(),
            lambda c, l, r: r["outcomes"].append(deepcopy(r["outcomes"][0])),
            lambda c, l, r: r["outcomes"][0]["baseline"]["suggestion"].update(path=["leaf"]),
            lambda c, l, r: r["outcomes"][0].update(status="unattempted"),
            lambda c, l, r: r["outcomes"][0].update(status="failed"),
            lambda c, l, r: r["outcomes"][0].update(treatment=answer("other")),
            lambda c, l, r: l["labels"].pop(),
        ):
            cases, labels, replay = fixture()
            mutate(cases, labels, replay)
            with self.assertRaises(ValueError):
                aggregate(cases, labels, replay)

    def test_attempt_evidence_rejects_empty_duplicate_order_hash_and_unrelated(self):
        for mutate in (
            lambda r: r.update(attempts=[]),
            lambda r: r.update(attempts=[deepcopy(r["attempts"][0])] * 48),
            lambda r: r["attempts"].reverse(),
            lambda r: r["attempts"][0].update(requestSha256="0" * 64),
            lambda r: r["attempts"][0].update(caseId="unrelated"),
            lambda r: r["attempts"].pop(),
            lambda r: r.update(reservationNanodollars=0),
        ):
            cases, labels, replay = fixture()
            mutate(replay)
            with self.assertRaises(ValueError):
                aggregate(cases, labels, replay)

    def test_legitimate_global_abstentions_skip_selected_attempts(self):
        cases, labels, replay = fixture()
        for row in replay["outcomes"]:
            row.update(baseline=answer(None), treatment=answer(None))
        account(replay)
        report = aggregate(cases, labels, replay)
        self.assertEqual(len(replay["attempts"]), 24)
        self.assertEqual(report["selected_path_attempts"], 0)
        self.assertEqual(report["arms"]["baseline"]["counts"]["abstained"], 24)

    def test_cli_aggregate_only_binding_and_exclusive_output(self):
        runner = Path(__file__).resolve().parents[2] / "tools/Receipts.JevProductionParity/bin/Debug/net10.0/Receipts.JevProductionParity.dll"
        if not runner.is_file():
            self.skipTest("build production parity tool to run socket-free CLI integration")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cases, labels, replay = fixture()
            paths = {k: root / (k + ".json") for k in ("cases", "labels", "replay", "freeze", "output", "exchanges")}
            for key, value in (("cases", cases), ("labels", labels), ("replay", replay)):
                paths[key].write_text(json.dumps(value), encoding="utf-8")
            paths["exchanges"].write_text("[]", encoding="utf-8")
            paths["replay"].unlink()
            generated = subprocess.run(["dotnet", str(runner), "offline-replay", str(paths["cases"]),
                                        str(paths["exchanges"]), str(paths["replay"])], capture_output=True)
            self.assertEqual(generated.returncode, 0)
            freeze = {"beforeCurrentPredictions": True, "cohortSize": 24,
                      "sha256": {binding: hashlib.sha256(paths[key].read_bytes()).hexdigest()
                                 for key, binding in (("cases", "membership"), ("labels", "adjudicatedLabels"))}}
            paths["freeze"].write_text(json.dumps(freeze), encoding="utf-8")
            command = [sys.executable, str(Path(__file__).with_name("aggregate_scoring.py"))]
            for key, path in paths.items():
                command.extend(["--" + key, str(path)])
            command.extend(["--runner", str(runner)])
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stdout, "")
            self.assertEqual(json.loads(paths["output"].read_text())["total"], 24)
            self.assertEqual(subprocess.run(command, capture_output=True).returncode, 2)
            paths["output"].unlink()
            paths["labels"].write_text('{"sensitive": "PRIVATE CONTENT",}', encoding="utf-8")
            # Rebind malformed synthetic bytes to exercise sanitized parse errors.
            freeze["sha256"]["adjudicatedLabels"] = hashlib.sha256(paths["labels"].read_bytes()).hexdigest()
            paths["freeze"].write_text(json.dumps(freeze), encoding="utf-8")
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(result.returncode, 2)
            self.assertNotIn("PRIVATE", result.stderr)
            self.assertNotIn(str(root), result.stderr)
            self.assertFalse(paths["output"].exists())

    def test_production_replay_rejects_other_evidence_and_tampered_outcome(self):
        from aggregate_scoring import verify_production_replay
        runner = Path(__file__).resolve().parents[2] / "tools/Receipts.JevProductionParity/bin/Debug/net10.0/Receipts.JevProductionParity.dll"
        if not runner.is_file():
            self.skipTest("build production parity tool for socket-free integration")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cases, _, _ = fixture()
            cohort, exchanges, output = (root / n for n in ("cases.json", "exchanges.json", "replay.json"))
            cohort.write_text(json.dumps(cases), encoding="utf-8")
            exchanges.write_text("[]", encoding="utf-8")
            subprocess.run(["dotnet", str(runner), "offline-replay", str(cohort), str(exchanges), str(output)],
                           check=True, capture_output=True)
            replay = json.loads(output.read_text())
            tampered = deepcopy(replay)
            tampered["outcomes"][0]["baseline"] = answer()
            with tempfile.TemporaryDirectory(dir=root) as scratch, self.assertRaises(ValueError):
                verify_production_replay(cohort, exchanges, runner, tampered, Path(scratch))
            cases[0]["request"]["description"] = "unrelated synthetic evidence"
            cohort.write_text(json.dumps(cases), encoding="utf-8")
            with tempfile.TemporaryDirectory(dir=root) as scratch, self.assertRaises(ValueError):
                verify_production_replay(cohort, exchanges, runner, replay, Path(scratch))


if __name__ == "__main__":
    unittest.main()
