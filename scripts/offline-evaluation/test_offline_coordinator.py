import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from campaign_ledger import CampaignLedger, LedgerError, Policy, RESERVATION_NANODOLLARS
from guarded_exchange import RecordedTransport
from offline_coordinator import OfflineCoordinator, UnresolvedStage

@unittest.skipUnless(os.name == 'nt', 'Windows fixed-disk ledger')
class CoordinatorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.path = self.root / 'ledger'
        with CampaignLedger(self.path, create_policy=Policy('synthetic', 3, 3 * RESERVATION_NANODOLLARS, 0)):
            pass
        self.a = ['case-a', 'baseline', 'production', 0]
        self.b = ['case-b', 'candidate', 'shortlist', 0]

    def test_crash_after_persist_reparses_without_send(self):
        code = '''import os, sys
from pathlib import Path
from campaign_ledger import CampaignLedger
from guarded_exchange import RecordedTransport
from offline_coordinator import OfflineCoordinator
root=Path(sys.argv[1])
def crash(response): os._exit(23)
with CampaignLedger(root/'ledger') as ledger:
    OfflineCoordinator(ledger,root).execute(['case-a','baseline','production',0],b'request',RecordedTransport(b'answer'),crash)
'''
        child = subprocess.run([sys.executable, '-c', code, str(self.root)], cwd=Path(__file__).parent,
                               capture_output=True, timeout=20, creationflags=subprocess.CREATE_NO_WINDOW)
        self.assertEqual(child.returncode, 23, child.stderr)
        with CampaignLedger(self.path) as ledger:
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)
            unused = RecordedTransport(b'wrong')
            runner = OfflineCoordinator(ledger, self.root)
            self.assertEqual(runner.execute(self.a, b'request', unused, bytes), b'answer')
            self.assertFalse(unused.used)
            self.assertEqual(runner.execute(self.a, b'request', None, bytes), b'answer')
            self.assertEqual(ledger.snapshot()['attempts'], 1)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 0)

    def test_unknown_does_not_retry_independent_case_continues_with_shared_budget(self):
        with CampaignLedger(self.path) as ledger:
            runner = OfflineCoordinator(ledger, self.root)
            with patch.object(runner, '_persist', side_effect=OSError('interrupted')):
                with self.assertRaises(OSError):
                    runner.execute(self.a, b'one', RecordedTransport(b'response'), bytes)
        with CampaignLedger(self.path) as ledger:
            runner = OfflineCoordinator(ledger, self.root)
            unused = RecordedTransport(b'wrong')
            with self.assertRaises(UnresolvedStage):
                runner.execute(self.a, b'one', unused, bytes)
            self.assertFalse(unused.used)
            same_case = RecordedTransport(b'blocked')
            with self.assertRaises(UnresolvedStage):
                runner.execute(['case-a', 'candidate', 'shortlist', 0], b'next', same_case, bytes)
            self.assertFalse(same_case.used)
            self.assertEqual(runner.execute(self.b, b'two', RecordedTransport(b'other'), bytes), b'other')
            runner.execute(['case-b', 'baseline', 'production', 0], b'three', RecordedTransport(b'third'), bytes)
            fourth = RecordedTransport(b'no')
            with self.assertRaises(LedgerError):
                runner.execute(['case-c', 'candidate', 'shortlist', 0], b'four', fourth, bytes)
            self.assertFalse(fourth.used)
            self.assertEqual(ledger.snapshot()['attempts'], 3)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)

    def test_parse_failure_reparse_changed_request_and_corrupt_response(self):
        with CampaignLedger(self.path) as ledger:
            runner = OfflineCoordinator(ledger, self.root)
            with self.assertRaises(ValueError):
                runner.execute(self.a, b'one', RecordedTransport(b'42'), lambda _: int('invalid'))
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)
        with CampaignLedger(self.path) as ledger:
            runner = OfflineCoordinator(ledger, self.root)
            with self.assertRaises(LedgerError):
                runner.execute(self.a, b'changed', None, int)
            self.assertEqual(runner.execute(self.a, b'one', None, int), 42)
            attempt = next(row['id'] for row in ledger.stage_history() if row['kind'] == 'attempt')
            (self.root / (attempt + '.response')).write_bytes(b'corrupt')
            unused = RecordedTransport(b'new')
            with self.assertRaises(LedgerError):
                runner.execute(self.a, b'one', unused, int)
            self.assertFalse(unused.used)

    def test_reservation_crash_and_partial_response_are_unknown(self):
        with CampaignLedger(self.path) as ledger:
            attempt = ledger.reserve_attempt(b'one', stage_id=self.a)
            (self.root / (attempt + '.partial')).write_bytes(b'incomplete')
        with CampaignLedger(self.path) as ledger:
            unused = RecordedTransport(b'retry')
            with self.assertRaises(UnresolvedStage):
                OfflineCoordinator(ledger, self.root).execute(self.a, b'one', unused, bytes)
            self.assertFalse(unused.used)
            self.assertEqual(ledger.snapshot()['attempts'], 1)

    def test_rejects_anonymous_prior_attempt(self):
        with CampaignLedger(self.path) as ledger:
            ledger.reserve_attempt(b'legacy')
            with self.assertRaises(LedgerError):
                OfflineCoordinator(ledger, self.root).execute(self.b, b'two', RecordedTransport(b'answer'), bytes)
            self.assertEqual(ledger.snapshot()['attempts'], 1)
