"""Synthetic accounting checks only; never reads private pilot artifacts."""

from copy import deepcopy
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from aggregate_scoring import aggregate, validate_freeze, verify_bindings

CARRY = 4_750_000_000
RUNNER = (Path(__file__).resolve().parents[2]
          / "tools/Receipts.JevProductionParity/bin/Debug/net10.0/Receipts.JevProductionParity.dll")


def freeze_for(size=24, carry=CARRY):
    return {"beforeCurrentPredictions": True, "cohortSize": size, "maximumAttempts": 2 * size,
            "priorCarryNanodollars": carry,
            "candidateGates": {"globalFit": 0.9, "selectedPathSupport": 0.8},
            "acceptance": {"minimumUsefulFraction": 0.5, "maximumUsefulSuppressionFraction": 0.1},
            "sha256": {"membership": "0" * 64, "adjudicatedLabels": "1" * 64, "policy": "2" * 64}}


def policy_for(freeze):
    """Synthetic packet policy in the parity tool's owner-label shape."""
    return {"packetKind": "owner-labels-v1", "reservationNanodollarsPerAttempt": 2_752_512,
            "baselineGate": 0.7, "liveAuthorized": False, "cases": freeze["cohortSize"],
            "maximumAttempts": freeze["maximumAttempts"],
            "conservativePriorCarryNanodollars": freeze["priorCarryNanodollars"],
            "cumulativeCeilingNanodollars": 5_000_000_000,
            "autoWriteGlobalGate": freeze["candidateGates"]["globalFit"],
            "autoWriteSelectedGate": freeze["candidateGates"]["selectedPathSupport"]}


def write_freeze(freeze, paths):
    """Bind the synthetic files on disk into a freeze written at paths["freeze"]."""
    freeze["sha256"] = {binding: hashlib.sha256(paths[key].read_bytes()).hexdigest() for key, binding in
                        (("cases", "membership"), ("labels", "adjudicatedLabels"), ("policy", "policy"))}
    paths["freeze"].write_text(json.dumps(freeze), encoding="utf-8")


def fixture(size=24):
    taxonomy = [{"id": k, "parentId": p, "name": k, "definition": "Synthetic " + k} for k, p in
                (("root", None), ("parent", "root"), ("leaf", "parent"), ("other", None))]
    cases = [{"id": str(i), "request": {"categories": taxonomy,
              "taxonomyRevision": "synthetic", "description": "SYNTHETIC SECRET"}} for i in range(size)]
    labels = {"oracle": "synthetic oracle", "labels": [{"caseId": c["id"], "status": "labelable",
                         "maximallySpecificAcceptablePaths": [{"categoryId": "leaf",
                                                               "pathIds": ["root", "parent", "leaf"]}],
                         "otherSupportedCoarseAlternatives": [],
                         "validBroadAncestorIds": ["parent"],
                         "initialPassDisagreement": False, "uncertainty": None} for c in cases]}
    replay = {"totalCases": size, "stopped": False, "attempts": [],
              "outcomes": [{"id": c["id"], "status": "completed",
                            "baseline": answer(), "treatment": treated()} for c in cases]}
    account(replay)
    return cases, labels, replay, freeze_for(size)


def account(replay, carry=CARRY):
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
                  priorCarryNanodollars=carry,
                  combinedReservationNanodollars=len(attempts) * 2_752_512 + carry)


def answer(key="leaf", fit=0.95):
    """Baseline-shaped production result (camelCase CategorySuggestion)."""
    paths = {"leaf": ["root", "parent", "leaf"], "parent": ["root", "parent"],
             "root": ["root"], "other": ["other"], None: []}
    return {"failure": None, "suggestion": {"categoryId": key, "path": paths[key],
                                            "abstentionReason": "no-fit" if key is None else None,
                                            "fitProbability": fit, "selectedPathSupport": None}}


def rejected(fit=0.95, support=0.6):
    """Treatment result when production selected-path support is below 0.7."""
    result = treated("leaf", fit, support)
    result["suggestion"].update(categoryId=None, path=[], abstentionReason="uncertain-selected-path")
    return result


def treated(key="leaf", fit=0.95, support=0.9):
    """Treatment result: the baseline with selected-path support (production keeps >= 0.7)."""
    result = answer(key, fit)
    if key is not None:
        result["suggestion"]["selectedPathSupport"] = support
    return result


class AggregateScoringTests(unittest.TestCase):
    def test_specific_broad_wrong_and_uncredited_ancestor(self):
        cases, labels, replay, freeze = fixture()
        for i, key in enumerate(("leaf", "parent", "other", "root")):
            replay["outcomes"][i]["baseline"] = answer(key)
            replay["outcomes"][i]["treatment"] = treated(key)
        result = aggregate(cases, labels, replay, freeze)
        counts = result["arms"]["treatment"]["counts"]
        self.assertEqual([counts[k] for k in ("most_specific", "broad", "wrong_branch",
                                              "other_unsupported", "accepted")], [21, 1, 1, 1, 22])
        self.assertEqual(result["arms"]["treatment"]["useful_coverage"]["denominator"], 24)
        self.assertEqual(sum(result["paired_transitions"]["baseline -> treatment"].values()), 24)
        self.assertEqual(result["arms"]["candidate"]["counts"], counts)
        self.assertEqual(result["oracle"], "synthetic oracle")
        self.assertNotIn("SYNTHETIC SECRET", json.dumps(result))
        self.assertNotIn("leaf", json.dumps(result))

    def test_harmful_specificity_and_no_answer(self):
        cases, labels, replay, freeze = fixture()
        labels["labels"][0]["maximallySpecificAcceptablePaths"] = [
            {"categoryId": "parent", "pathIds": ["root", "parent"]}]
        labels["labels"][0]["validBroadAncestorIds"] = []
        label = labels["labels"][1]
        label.update(status="abstain_required", maximallySpecificAcceptablePaths=[], validBroadAncestorIds=[])
        replay["outcomes"][1]["treatment"] = answer(None)
        result = aggregate(cases, labels, replay, freeze)
        self.assertEqual(result["strata"]["broad_only"], 1)
        self.assertEqual(result["arms"]["treatment"]["counts"]["harmful_specificity"], 1)
        self.assertEqual(result["arms"]["baseline"]["counts"]["writes_on_no_answer"], 1)
        self.assertEqual(result["arms"]["treatment"]["counts"]["correct_no_answer_abstention"], 1)

    def test_selected_failure_and_unknown_keep_full_denominator(self):
        for status in ("failed", "unknown"):
            cases, labels, replay, freeze = fixture()
            replay["stopped"] = True
            replay["outcomes"][0].update(status=status, treatment={"failure": "Timeout", "suggestion": None})
            for row in replay["outcomes"][1:]:
                row.update(status="unattempted", baseline=None, treatment=None)
            account(replay)
            report = aggregate(cases, labels, replay, freeze)
            self.assertEqual(report["arms"]["treatment"]["counts"][status], 1)
            self.assertEqual(report["arms"]["treatment"]["counts"]["unattempted"], 23)
            self.assertEqual(report["arms"]["treatment"]["counts"]["abstained"], 0)
            self.assertEqual(report["arms"]["baseline"]["useful_coverage"]["value"], 1 / 24)
            self.assertIsNone(report["arms"]["treatment"]["selective_accuracy"]["value"])

    def test_unresolved_and_disagreement_sensitivity(self):
        cases, labels, replay, freeze = fixture()
        labels["labels"][0].update(status="unresolved", maximallySpecificAcceptablePaths=[],
                                     validBroadAncestorIds=[], initialPassDisagreement=True)
        labels["labels"][1].update(initialPassDisagreement=True, uncertainty="synthetic doubt")
        result = aggregate(cases, labels, replay, freeze)
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
            lambda c, l, r: r["outcomes"][0].update(treatment=treated("other")),
            lambda c, l, r: l["labels"].pop(),
        ):
            cases, labels, replay, freeze = fixture()
            mutate(cases, labels, replay)
            with self.assertRaises(ValueError):
                aggregate(cases, labels, replay, freeze)

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
            cases, labels, replay, freeze = fixture()
            mutate(replay)
            with self.assertRaises(ValueError):
                aggregate(cases, labels, replay, freeze)

    def test_legitimate_global_abstentions_skip_selected_attempts(self):
        cases, labels, replay, freeze = fixture()
        for row in replay["outcomes"]:
            row.update(baseline=answer(None, 0.2), treatment=answer(None, 0.2))
        account(replay)
        report = aggregate(cases, labels, replay, freeze)
        self.assertEqual(len(replay["attempts"]), 24)
        self.assertEqual(report["selected_path_attempts"], 0)
        self.assertEqual(report["arms"]["baseline"]["counts"]["abstained"], 24)

    def test_small_cohort_uses_frozen_size_and_attempt_cap(self):
        cases, labels, replay, freeze = fixture(4)
        self.assertEqual(len(replay["attempts"]), 8)
        report = aggregate(cases, labels, replay, freeze)
        self.assertEqual(report["total"], 4)
        self.assertEqual(report["arms"]["treatment"]["useful_coverage"]["denominator"], 4)
        self.assertEqual(report["gates"]["treatment"]["thresholds"]["minimumUseful"], 2)
        for mutate in (
            lambda c, l, r, f: f.update(cohortSize=5, maximumAttempts=10),
            lambda c, l, r, f: r.update(totalCases=24),
            lambda c, l, r, f: (f.update(priorCarryNanodollars=0)),
            lambda c, l, r, f: r["attempts"].append(deepcopy(r["attempts"][-1])),
        ):
            cases, labels, replay, freeze = fixture(4)
            mutate(cases, labels, replay, freeze)
            with self.assertRaises(ValueError):
                aggregate(cases, labels, replay, freeze)
        cases, labels, replay, _ = fixture()
        with self.assertRaises(ValueError):
            aggregate(cases, labels, replay, freeze_for(4))

    def test_freeze_validation_fails_closed(self):
        self.assertEqual(validate_freeze(freeze_for(48))["maximumAttempts"], 96)
        for mutate in (
            lambda f: f.update(cohortSize=0, maximumAttempts=0),
            lambda f: f.update(cohortSize=49, maximumAttempts=98),
            lambda f: f.update(cohortSize=True, maximumAttempts=2),
            lambda f: f.update(cohortSize=24.0),
            lambda f: f.update(maximumAttempts=47),
            lambda f: f.update(beforeCurrentPredictions=False),
            lambda f: f.update(priorCarryNanodollars=-1),
            lambda f: f.update(priorCarryNanodollars=4.75e9),
            lambda f: f.update(extra=1),
            lambda f: f.pop("acceptance"),
            lambda f: f["candidateGates"].update(globalFit=0.69),
            lambda f: f["candidateGates"].update(selectedPathSupport=1.01),
            lambda f: f["candidateGates"].update(globalFit=float("nan")),
            lambda f: f["candidateGates"].update(globalFit=True),
            lambda f: f["candidateGates"].pop("selectedPathSupport"),
            lambda f: f["acceptance"].update(minimumUsefulFraction=0),
            lambda f: f["acceptance"].update(maximumUsefulSuppressionFraction=1.5),
            lambda f: f["sha256"].update(policy="A" * 64),
            lambda f: f["sha256"].pop("policy"),
        ):
            freeze = freeze_for()
            mutate(freeze)
            with self.assertRaises(ValueError):
                validate_freeze(freeze)

    def test_candidate_requires_both_preregistered_signals(self):
        cases, labels, replay, freeze = fixture(8)
        rows = replay["outcomes"]
        rows[0].update(baseline=answer(fit=0.9), treatment=treated(fit=0.9, support=0.8))  # boundaries
        rows[1].update(baseline=answer(fit=0.85), treatment=treated(fit=0.85, support=0.99))
        rows[2].update(treatment=treated(support=0.75))
        rows[3].update(treatment=rejected())
        del rows[4]["treatment"]["suggestion"]["selectedPathSupport"]
        rows[5].update(baseline=answer("other"), treatment=treated("other", support=0.85))
        rows[6].update(baseline=answer("other", 0.8), treatment=treated("other", 0.8))
        rows[7].update(baseline=answer(None, 0.2), treatment=answer(None, 0.2))
        account(replay)
        report = aggregate(cases, labels, replay, freeze)
        treatment, candidate = (report["arms"][arm]["counts"] for arm in ("treatment", "candidate"))
        self.assertEqual([treatment[k] for k in ("most_specific", "wrong_branch", "abstained")], [4, 2, 2])
        self.assertEqual([candidate[k] for k in ("most_specific", "wrong_branch", "abstained")], [1, 1, 6])
        self.assertEqual(candidate["useful_baseline_suppressed"], 4)
        self.assertEqual(candidate["baseline_errors_suppressed"], 1)
        self.assertEqual(treatment["useful_baseline_suppressed"], 1)
        self.assertEqual(treatment["baseline_errors_suppressed"], 0)
        self.assertEqual(report["paired_transitions"]["baseline -> candidate"],
                         {"most_specific -> most_specific": 1, "most_specific -> abstained": 4,
                          "wrong_branch -> wrong_branch": 1, "wrong_branch -> abstained": 1,
                          "abstained -> abstained": 1})
        self.assertEqual(report["candidate_gates"], {"globalFit": 0.9, "selectedPathSupport": 0.8})

    def test_candidate_mirrors_non_completed_treatment(self):
        cases, labels, replay, freeze = fixture(4)
        replay["stopped"] = True
        replay["outcomes"][1].update(status="unknown", treatment={"failure": "Timeout", "suggestion": None})
        for row in replay["outcomes"][2:]:
            row.update(status="unattempted", baseline=None, treatment=None)
        account(replay)
        counts = aggregate(cases, labels, replay, freeze)["arms"]["candidate"]["counts"]
        self.assertEqual([counts[k] for k in ("most_specific", "unknown", "unattempted", "abstained")],
                         [1, 1, 2, 0])

    def test_gates_untested_for_owner_label_strata(self):
        cases, labels, replay, freeze = fixture()
        gates = aggregate(cases, labels, replay, freeze)["gates"]
        for arm in ("candidate", "treatment"):
            self.assertEqual({k: gates[arm][k] for k in (
                "complete", "minimum_useful", "zero_incorrect_writes", "retention",
                "no_answer_safety", "broad_only_safety", "overall")},
                {"complete": "pass", "minimum_useful": "pass", "zero_incorrect_writes": "pass",
                 "retention": "pass", "no_answer_safety": "untested",
                 "broad_only_safety": "untested", "overall": "incomplete"})
            self.assertIn("synthetic-safety", gates[arm]["untestedReason"])

    def test_gates_pass_only_when_safety_strata_are_covered(self):
        cases, labels, replay, freeze = fixture()
        labels["labels"][0].update(maximallySpecificAcceptablePaths=[
            {"categoryId": "parent", "pathIds": ["root", "parent"]}], validBroadAncestorIds=[])
        labels["labels"][1].update(status="abstain_required", maximallySpecificAcceptablePaths=[],
                                   validBroadAncestorIds=[])
        replay["outcomes"][0].update(baseline=answer("parent"), treatment=treated("parent"))
        replay["outcomes"][1].update(treatment=rejected())
        report = aggregate(cases, labels, replay, freeze)
        for arm in ("candidate", "treatment"):
            self.assertEqual(report["gates"][arm]["overall"], "pass")
            self.assertIsNone(report["gates"][arm]["untestedReason"])
        self.assertEqual(report["arms"]["candidate"]["counts"]["writes_on_broad_only"], 1)
        self.assertEqual(report["arms"]["candidate"]["counts"]["harmful_specificity_on_broad_only"], 0)
        # An unsupported descendant on the broad-only case fails safety and correctness.
        replay["outcomes"][0].update(treatment=treated("leaf"), baseline=answer("leaf"))
        gates = aggregate(cases, labels, replay, freeze)["gates"]["candidate"]
        self.assertEqual([gates[k] for k in ("broad_only_safety", "zero_incorrect_writes", "overall")],
                         ["fail", "fail", "fail"])
        replay["outcomes"][0].update(treatment=treated("parent"), baseline=answer("parent"))
        replay["outcomes"][1].update(treatment=treated())
        gates = aggregate(cases, labels, replay, freeze)["gates"]["treatment"]
        self.assertEqual([gates[k] for k in ("no_answer_safety", "overall")], ["fail", "fail"])

    def test_retention_minimum_and_completion_gates(self):
        for suppressed, expected in ((2, "pass"), (3, "fail")):  # floor(0.1 * 24) == 2
            cases, labels, replay, freeze = fixture()
            for row in replay["outcomes"][:suppressed]:
                row["treatment"] = rejected()
            account(replay)
            gates = aggregate(cases, labels, replay, freeze)["gates"]["treatment"]
            self.assertEqual(gates["retention"], expected)
            self.assertEqual(gates["thresholds"]["maximumUsefulSuppressed"], 2)
        cases, labels, replay, freeze = fixture(4)
        for row in replay["outcomes"][1:]:
            row.update(baseline=answer(None, 0.2), treatment=answer(None, 0.2))
        account(replay)
        self.assertEqual(aggregate(cases, labels, replay, freeze)["gates"]["candidate"]["minimum_useful"],
                         "fail")
        cases, labels, replay, freeze = fixture(4)
        replay["stopped"] = True
        replay["outcomes"][3].update(status="failed", treatment={"failure": "Timeout", "suggestion": None})
        account(replay)
        gates = aggregate(cases, labels, replay, freeze)["gates"]["candidate"]
        self.assertEqual([gates["complete"], gates["overall"]], ["fail", "fail"])
        cases, labels, replay, freeze = fixture(4)
        labels["labels"][0].update(status="unresolved", maximallySpecificAcceptablePaths=[],
                                   validBroadAncestorIds=[])
        self.assertEqual(aggregate(cases, labels, replay, freeze)["gates"]["treatment"]["complete"], "fail")

    def test_oracle_string_required(self):
        for value in (None, "", "   ", 7):
            cases, labels, replay, freeze = fixture(4)
            labels["oracle"] = value
            with self.assertRaises(ValueError):
                aggregate(cases, labels, replay, freeze)
        cases, labels, replay, freeze = fixture(4)
        del labels["oracle"]
        with self.assertRaises(ValueError):
            aggregate(cases, labels, replay, freeze)

    def test_policy_hash_mismatch_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            paths = {k: root / (k + ".json") for k in ("cases", "labels", "policy", "freeze")}
            freeze = freeze_for()
            for key in ("cases", "labels"):
                paths[key].write_text(json.dumps({"synthetic": key}), encoding="utf-8")
            paths["policy"].write_text(json.dumps(policy_for(freeze)), encoding="utf-8")
            write_freeze(freeze, paths)
            verify_bindings(freeze, paths["cases"], paths["labels"], paths["policy"])
            paths["policy"].write_text(json.dumps({**policy_for(freeze), "liveAuthorized": True}),
                                       encoding="utf-8")
            with self.assertRaises(ValueError):
                verify_bindings(freeze, paths["cases"], paths["labels"], paths["policy"])
            with self.assertRaises(ValueError):
                verify_bindings(freeze, paths["cases"], paths["labels"], paths["cases"])

    def test_freeze_must_score_the_policy_rule_cohort_and_budget(self):
        for field, value in (("autoWriteGlobalGate", 0.85), ("autoWriteSelectedGate", 0.7),
                             ("cases", 23), ("maximumAttempts", 46),
                             ("conservativePriorCarryNanodollars", 0)):
            with tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                paths = {k: root / (k + ".json") for k in ("cases", "labels", "policy", "freeze")}
                freeze = freeze_for()
                for key in ("cases", "labels"):
                    paths[key].write_text(json.dumps({"synthetic": key}), encoding="utf-8")
                paths["policy"].write_text(json.dumps({**policy_for(freeze), field: value}),
                                           encoding="utf-8")
                write_freeze(freeze, paths)
                with self.assertRaises(ValueError):
                    verify_bindings(freeze, paths["cases"], paths["labels"], paths["policy"])

    def test_cli_aggregate_only_binding_and_exclusive_output(self):
        runner = RUNNER
        if not runner.is_file():
            self.skipTest("build production parity tool to run socket-free CLI integration")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cases, labels, replay, freeze = fixture()
            paths = {k: root / (k + ".json") for k in
                     ("cases", "labels", "policy", "replay", "freeze", "output", "exchanges")}
            for key, value in (("cases", cases), ("labels", labels), ("policy", policy_for(freeze))):
                paths[key].write_text(json.dumps(value), encoding="utf-8")
            paths["exchanges"].write_text("[]", encoding="utf-8")
            generated = subprocess.run(["dotnet", str(runner), "offline-replay", str(paths["policy"]),
                                        str(paths["cases"]), str(paths["exchanges"]), str(paths["replay"])],
                                       capture_output=True)
            self.assertEqual(generated.returncode, 0)
            write_freeze(freeze, paths)
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
            write_freeze(freeze, paths)
            result = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(result.returncode, 2)
            self.assertNotIn("PRIVATE", result.stderr)
            self.assertNotIn(str(root), result.stderr)
            self.assertFalse(paths["output"].exists())

    def test_production_replay_rejects_other_evidence_and_tampered_outcome(self):
        from aggregate_scoring import verify_production_replay
        runner = RUNNER
        if not runner.is_file():
            self.skipTest("build production parity tool for socket-free integration")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cases, _, _, freeze = fixture()
            policy, cohort, exchanges, output = (root / n for n in (
                "policy.json", "cases.json", "exchanges.json", "replay.json"))
            policy.write_text(json.dumps(policy_for(freeze)), encoding="utf-8")
            cohort.write_text(json.dumps(cases), encoding="utf-8")
            exchanges.write_text("[]", encoding="utf-8")
            subprocess.run(["dotnet", str(runner), "offline-replay", str(policy), str(cohort),
                            str(exchanges), str(output)], check=True, capture_output=True)
            replay = json.loads(output.read_text())
            tampered = deepcopy(replay)
            tampered["outcomes"][0]["baseline"] = answer()
            with tempfile.TemporaryDirectory(dir=root) as scratch, self.assertRaises(ValueError):
                verify_production_replay(policy, cohort, exchanges, runner, tampered, Path(scratch))
            cases[0]["request"]["description"] = "unrelated synthetic evidence"
            cohort.write_text(json.dumps(cases), encoding="utf-8")
            with tempfile.TemporaryDirectory(dir=root) as scratch, self.assertRaises(ValueError):
                verify_production_replay(policy, cohort, exchanges, runner, replay, Path(scratch))


if __name__ == "__main__":
    unittest.main()
