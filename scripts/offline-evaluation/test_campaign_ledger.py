import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from campaign_ledger import CampaignLedger, LedgerBusy, LedgerError, Policy, RESERVATION_NANODOLLARS


@unittest.skipUnless(os.name == "nt", "ledger deliberately supports Windows fixed disks only")
class CampaignLedgerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "campaign.jsonl"

    def policy(self, calls=3, budget=None, carry=0):
        return Policy("synthetic-only", calls,
                      budget if budget is not None else calls * RESERVATION_NANODOLLARS + carry,
                      carry)

    def child(self, code, *args):
        return subprocess.Popen(
            [sys.executable, "-c", code, str(self.path), *args],
            cwd=Path(__file__).parent, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, creationflags=subprocess.CREATE_NO_WINDOW)

    def harvest(self, child):
        try:
            stdout, stderr = child.communicate(timeout=20)
        except BaseException:
            child.kill()
            child.communicate()
            raise
        self.assertEqual(child.returncode, 0, stderr)
        return stdout.strip()

    def test_reservation_is_persisted_before_return_and_only_hash_is_stored(self):
        payload = b'{"fictional":"synthetic example"}'
        with CampaignLedger(self.path, create_policy=self.policy()) as ledger:
            attempt_id = ledger.reserve_attempt(payload)
            rows = [json.loads(line) for line in self.path.read_text().splitlines()]
            self.assertEqual(rows[-1]["body"]["id"], attempt_id)
            self.assertNotIn("synthetic example", self.path.read_text())
            self.assertEqual(ledger.snapshot()["reserved_nanodollars"], RESERVATION_NANODOLLARS)

    def test_named_reservations_survive_restart_and_share_allowance(self):
        first = ["case-a", "baseline", "production", 0]
        second = ["case-b", "candidate", "shortlist", 0]
        with CampaignLedger(self.path, create_policy=self.policy(2)) as ledger:
            attempt = ledger.reserve_attempt(b"request-a", stage_id=first)
            self.assertEqual(ledger.stage_history()[-1]["stage_id"], first)
        with CampaignLedger(self.path) as ledger:
            with self.assertRaises(LedgerError):
                ledger.reserve_attempt(b"different request", stage_id=first)
            ledger.reserve_attempt(b"request-b", stage_id=second)
            self.assertEqual(ledger.snapshot()["missing_outcomes"], 2)
            ledger.record_outcome(attempt, "unknown")
            with self.assertRaises(LedgerError):
                ledger.reserve_attempt(b"request-c", stage_id=["case-a", "candidate", "shortlist", 0])
            self.assertEqual(ledger.snapshot()["attempts"], 2)

    def test_named_reservation_validates_identity_before_charging(self):
        with CampaignLedger(self.path, create_policy=self.policy(2)) as ledger:
            for invalid in (["case", "arm", "phase", True], ["case", "arm", "phase", -1],
                            ["case", "arm", "phase", 96], ["case", "arm", "phase"],
                            ["case/escape", "arm", "phase", 0], ["x" * 33, "arm", "phase", 0]):
                with self.subTest(invalid=invalid), self.assertRaises(LedgerError):
                    ledger.reserve_attempt(b"request", stage_id=invalid)
            self.assertEqual(ledger.snapshot()["attempts"], 0)

    def test_all_statuses_and_missing_usage_retain_full_reservation(self):
        with CampaignLedger(self.path, create_policy=self.policy(4, carry=10)) as ledger:
            for status in ("success", "error", "unknown"):
                ledger.record_outcome(ledger.reserve_attempt(b"fictional"), status)
            ledger.reserve_attempt(b"missing outcome")
            self.assertEqual(ledger.snapshot()["reserved_nanodollars"], 4 * RESERVATION_NANODOLLARS + 10)
            self.assertEqual(ledger.snapshot()["missing_outcomes"], 1)
            with self.assertRaises(LedgerError):
                ledger.reserve_attempt(b"over cap")

    def test_spend_cap_includes_unknown_carry_and_exact_boundary(self):
        with CampaignLedger(self.path, create_policy=self.policy(3, RESERVATION_NANODOLLARS + 7, 7)) as ledger:
            ledger.reserve_attempt(b"fictional")
            with self.assertRaises(LedgerError):
                ledger.reserve_attempt(b"another")
        with CampaignLedger(self.path) as ledger:
            self.assertEqual(ledger.snapshot()["attempts"], 1)
            with self.assertRaises(LedgerError):
                ledger.reserve_attempt(b"restart cannot reset cap")

    def test_crashed_process_leaves_attempt_charged_and_lock_recoverable(self):
        with CampaignLedger(self.path, create_policy=self.policy(1)):
            pass
        code = ("import os,sys; from campaign_ledger import CampaignLedger; "
                "ledger=CampaignLedger(sys.argv[1]); ledger.reserve_attempt(b'fictional'); os._exit(0)")
        self.harvest(self.child(code))
        with CampaignLedger(self.path) as ledger:
            self.assertEqual(ledger.snapshot()["missing_outcomes"], 1)
            with self.assertRaises(LedgerError):
                ledger.reserve_attempt(b"restart")

    def test_process_lock_fails_immediately_and_is_released_on_close(self):
        code = """import sys
from campaign_ledger import CampaignLedger, LedgerBusy
try:
    with CampaignLedger(sys.argv[1]): print('opened')
except LedgerBusy:
    print('busy')
"""
        with CampaignLedger(self.path, create_policy=self.policy()):
            self.assertEqual(self.harvest(self.child(code)), "busy")
        self.assertEqual(self.harvest(self.child(code)), "opened")

    def test_competing_processes_cannot_overbook_last_slot(self):
        with CampaignLedger(self.path, create_policy=self.policy(1)):
            pass
        code = """import sys
from campaign_ledger import CampaignLedger, LedgerError
try:
    with CampaignLedger(sys.argv[1]) as ledger: ledger.reserve_attempt(b'fictional')
    print('reserved')
except LedgerError:
    print('denied')
"""
        children = [self.child(code) for _ in range(8)]
        outputs = [self.harvest(child) for child in children]
        self.assertEqual(outputs.count("reserved"), 1)
        with CampaignLedger(self.path) as ledger:
            self.assertEqual(ledger.snapshot()["attempts"], 1)

    def test_uncertain_fsync_never_returns_permission_or_releases_attempt(self):
        with CampaignLedger(self.path, create_policy=self.policy(1)) as ledger:
            with patch("campaign_ledger.os.fsync", side_effect=OSError("synthetic fsync error")):
                with self.assertRaises(OSError):
                    ledger.reserve_attempt(b"fictional")
            with self.assertRaises(LedgerError):
                ledger.snapshot()
        # If bytes survived, keep them charged. If a real crash tore the write,
        # reopening instead fails closed (covered separately).
        with CampaignLedger(self.path) as ledger:
            self.assertEqual(ledger.snapshot()["attempts"], 1)

    def test_partial_or_corrupted_journal_is_not_repaired(self):
        with CampaignLedger(self.path, create_policy=self.policy()) as ledger:
            ledger.reserve_attempt(b"fictional")
        original = self.path.read_bytes()
        for damaged in (b"", original[:-1], original + b"{", original.replace(b"fictional", b"changed"),
                        original.replace(b'"sequence":1', b'"sequence":2'),
                        original.replace(b'"sequence":0', b'"sequence":0,"sequence":0')):
            if damaged == original:
                continue
            with self.subTest(damaged=damaged[:25]):
                self.path.write_bytes(damaged)
                with self.assertRaises(LedgerError):
                    CampaignLedger(self.path)
                self.assertEqual(self.path.read_bytes(), damaged)

    def test_creation_is_explicit_and_cannot_reset_existing_journal(self):
        with self.assertRaises(FileNotFoundError):
            CampaignLedger(self.path)
        with CampaignLedger(self.path, create_policy=self.policy()) as ledger:
            ledger.reserve_attempt(b"fictional")
        with self.assertRaises(FileExistsError):
            CampaignLedger(self.path, create_policy=self.policy())
        with CampaignLedger(self.path) as ledger:
            self.assertEqual(ledger.snapshot()["attempts"], 1)

    def test_byte_cap_counts_utf8_bytes_and_rejects_without_reservation(self):
        policy = Policy("synthetic", 2, 2 * RESERVATION_NANODOLLARS, 0, max_payload_bytes=3)
        with CampaignLedger(self.path, create_policy=policy) as ledger:
            for invalid in (b"", "text", "éé".encode()):
                with self.assertRaises(LedgerError):
                    ledger.reserve_attempt(invalid)
            ledger.reserve_attempt("é".encode())
            self.assertEqual(ledger.snapshot()["attempts"], 1)

    def test_policy_and_duplicate_outcomes_are_rejected(self):
        for policy in (self.policy(0), self.policy(97), self.policy(budget=5_000_000_001),
                       self.policy(budget=1, carry=2), self.policy(True)):
            with self.assertRaises(LedgerError):
                CampaignLedger(self.path, create_policy=policy)
        with CampaignLedger(self.path, create_policy=self.policy()) as ledger:
            attempt_id = ledger.reserve_attempt(b"fictional")
            with self.assertRaises(LedgerError):
                ledger.record_outcome(attempt_id, "retry")
            ledger.record_outcome(attempt_id, "error")
            with self.assertRaises(LedgerError):
                ledger.record_outcome(attempt_id, "success")


if __name__ == "__main__":
    unittest.main()
