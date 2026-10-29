"""Synthetic journal extraction and socket-free scoring checks."""

from copy import deepcopy
import json
import hashlib
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from journal_exchanges import extract


class JournalExchangeTests(unittest.TestCase):
    def fixture(self):
        digest = "a" * 64
        rows = [{"kind": "header", "identity": "synthetic"},
                {"kind": "intent", "index": 0, "requestSha256": digest,
                 "reservationNanodollars": 2_752_512},
                {"kind": "response", "index": 0, "requestSha256": digest,
                 "body": "synthetic", "statusCode": 429}]
        replay = {"attempts": [{"requestSha256": digest, "status": "replayed"}]}
        return rows, replay

    def test_exact_body_status_and_fingerprint_are_preserved(self):
        rows, replay = self.fixture()
        self.assertEqual(extract(rows, replay), [{"body": "synthetic", "statusCode": 429,
                                                 "requestSha256": "a" * 64}])

    def test_only_reserved_missing_tail_can_remain_unknown(self):
        rows, replay = self.fixture()
        rows.pop()
        replay["attempts"][0]["status"] = "unknown"
        self.assertEqual(extract(rows, replay), [])
        replay["attempts"][0]["status"] = "replayed"
        with self.assertRaises(ValueError):
            extract(rows, replay)

    def test_drift_reordering_missing_reservations_and_extra_rows_rejected(self):
        for mutate in (
            lambda r, p: r[1].update(index=1),
            lambda r, p: r[2].update(requestSha256="b" * 64),
            lambda r, p: r[1].update(reservationNanodollars=0),
            lambda r, p: r.append(deepcopy(r[2])),
            lambda r, p: p["attempts"].append(deepcopy(p["attempts"][0])),
            lambda r, p: p["attempts"][0].update(requestSha256="b" * 64),
            lambda r, p: p["attempts"][0].update(status="unknown"),
        ):
            rows, replay = self.fixture()
            mutate(rows, replay)
            with self.assertRaises(ValueError):
                extract(rows, replay)

    def test_full_conditional_journal_and_unknown_tail_feed_bound_scorer(self):
        from test_aggregate_scoring import fixture
        runner = Path(__file__).resolve().parents[2] / "tools/Receipts.JevProductionParity/bin/Debug/net10.0/Receipts.JevProductionParity.dll"
        if not runner.is_file():
            self.skipTest("build production parity tool for socket-free integration")
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            cases, labels, _ = fixture()
            for case in cases:
                case["request"]["categories"] = [{"id": "food", "parentId": None,
                    "name": "Food", "definition": "Synthetic edible goods"}]
            for label in labels["labels"]:
                label.update(maximallySpecificAcceptablePaths=[{"categoryId": "food", "pathIds": ["food"]}],
                             validBroadAncestorIds=[])
            paths = {k: root / (k + ".json") for k in ("cases", "labels", "replay", "exchanges", "freeze", "output")}
            paths["cases"].write_text(json.dumps(cases), encoding="utf-8")
            paths["labels"].write_text(json.dumps(labels), encoding="utf-8")
            exchanges, rows = [], [{"kind": "header", "identity": "synthetic"}]
            while True:
                paths["exchanges"].write_text(json.dumps(exchanges), encoding="utf-8")
                paths["replay"].unlink(missing_ok=True)
                result = subprocess.run(["dotnet", str(runner), "offline-replay", str(paths["cases"]),
                                         str(paths["exchanges"]), str(paths["replay"])], capture_output=True)
                self.assertEqual(result.returncode, 0)
                replay = json.loads(paths["replay"].read_text())
                if not replay["stopped"]:
                    break
                attempt = replay["attempts"][-1]
                self.assertEqual(attempt["status"], "unknown")
                questions = json.loads(attempt["requestJson"])["questions"]
                answers = {key: ({"type": "choice", "choice": "food", "confidence": 0.9,
                                  "probabilities": {"food": 1.0}} if key == "category"
                                 else {"type": "noul", "noul": 0.9}) for key in questions}
                body = json.dumps({"model": "jev-1.14.0", "answers": answers,
                                   "usage": {"input_tokens": 100, "output_tokens": 10}})
                index, digest = len(exchanges), attempt["requestSha256"]
                rows.extend([{"kind": "intent", "index": index, "requestSha256": digest,
                              "reservationNanodollars": 2_752_512},
                             {"kind": "response", "index": index, "requestSha256": digest,
                              "body": body, "statusCode": 200}])
                exchanges.append({"body": body, "statusCode": 200, "requestSha256": digest})
                self.assertLessEqual(len(exchanges), 48)
            self.assertEqual(len(exchanges), 48)
            journal = root / "journal.jsonl"
            journal.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")
            paths["exchanges"].unlink()
            command = [sys.executable, str(Path(__file__).with_name("journal_exchanges.py")),
                       "--journal", str(journal), "--replay", str(paths["replay"]), "--output", str(paths["exchanges"])]
            result = subprocess.run(command, capture_output=True)
            self.assertEqual(result.returncode, 0)
            self.assertEqual(result.stdout, b"")
            freeze = {"beforeCurrentPredictions": True, "cohortSize": 24, "sha256": {
                binding: hashlib.sha256(paths[key].read_bytes()).hexdigest() for key, binding in
                (("cases", "membership"), ("labels", "adjudicatedLabels"))}}
            paths["freeze"].write_text(json.dumps(freeze), encoding="utf-8")
            scorer = [sys.executable, str(Path(__file__).with_name("aggregate_scoring.py"))]
            for key, path in paths.items():
                scorer.extend(["--" + key, str(path)])
            scorer.extend(["--runner", str(runner)])
            self.assertEqual(subprocess.run(scorer, capture_output=True).returncode, 0)
            report = json.loads(paths["output"].read_text())
            self.assertEqual(report["selected_path_attempts"], 24)
            self.assertEqual(report["arms"]["treatment"]["counts"]["accepted"], 24)
            self.assertEqual(subprocess.run(command, capture_output=True).returncode, 2)
            paths["exchanges"].write_text(json.dumps(exchanges[:-1]), encoding="utf-8")
            paths["replay"].unlink()
            subprocess.run(["dotnet", str(runner), "offline-replay", str(paths["cases"]),
                            str(paths["exchanges"]), str(paths["replay"])], check=True, capture_output=True)
            journal.write_text("".join(json.dumps(row) + "\n" for row in rows[:-1]), encoding="utf-8")
            paths["exchanges"].unlink()
            self.assertEqual(subprocess.run(command, capture_output=True).returncode, 0)
            paths["output"].unlink()
            self.assertEqual(subprocess.run(scorer, capture_output=True).returncode, 0)
            report = json.loads(paths["output"].read_text())
            self.assertEqual(report["arms"]["treatment"]["counts"]["unknown"], 1)
            self.assertEqual(report["selected_path_attempts"], 24)
            paths["exchanges"].unlink()
            journal.write_text('{"private":"SYNTHETIC SECRET"}', encoding="utf-8")
            result = subprocess.run(command, capture_output=True)
            self.assertEqual(result.returncode, 2)
            self.assertNotIn(b"SECRET", result.stderr)
            self.assertFalse(paths["exchanges"].exists())


if __name__ == "__main__":
    unittest.main()
