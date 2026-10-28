import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

from campaign_ledger import CampaignLedger, LedgerError, Policy, RESERVATION_NANODOLLARS
from guarded_exchange import RecordedTransport, exchange_recorded, validate_envelope, verify_artifacts


PAYLOAD = b'{"model":"jev-1.14.0","state":"invented apples","questions":{"fit":{"type":"noul"}}}'
REPLY = b'{"model":"jev-1.14.0","answers":{"fit":{"type":"noul","noul":0.9}},"usage":{"input_tokens":20,"output_tokens":2}}'


class EnvelopeTests(unittest.TestCase):
    def test_malformed_usage_model_and_membership_stop(self):
        for raw in (b'{}', REPLY.replace(b'20', b'null'), REPLY.replace(b'20', b'true'),
                    REPLY.replace(b'20', b'65537'), REPLY.replace(b'1.14.0', b'latest'),
                    REPLY.replace(b'"fit":', b'"wrong":'), REPLY.replace(b'0.9', b'NaN')):
            with self.subTest(raw=raw), self.assertRaises((ValueError, TypeError, KeyError)):
                validate_envelope(PAYLOAD, raw)


@unittest.skipUnless(os.name == 'nt', 'durable ledger requires Windows local fixed disk')
class GuardedRecordedTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.journal = self.root / 'campaign.jsonl'
        self.artifact = self.root / 'synthetic-artifact'
        self.artifact.write_bytes(b'synthetic executable/source identity')
        group = {str(self.artifact): hashlib.sha256(self.artifact.read_bytes()).hexdigest()}
        self.artifacts = {'source': group, 'executable': group}
        with CampaignLedger(self.journal, create_policy=Policy('synthetic', 3, 5_000_000_000, 0)):
            pass

    def exchange(self, ledger, reply=REPLY, output='exchange'):
        return exchange_recorded(ledger, PAYLOAD, RecordedTransport(reply), artifacts=self.artifacts,
                                 private_output=self.root / output)

    def test_exact_bytes_and_usage_are_private_and_reservations_retained(self):
        with CampaignLedger(self.journal) as ledger:
            receipt = self.exchange(ledger)
            self.assertEqual(receipt['network_calls'], 0)
            self.assertEqual((self.root / 'exchange/request.json').read_bytes(), PAYLOAD)
            self.assertEqual((self.root / 'exchange/response.json').read_bytes(), REPLY)
            self.assertEqual(ledger.snapshot()['reserved_nanodollars'], RESERVATION_NANODOLLARS)
        with CampaignLedger(self.journal) as ledger:
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 0)

    def test_bad_response_stops_after_restart(self):
        with CampaignLedger(self.journal) as ledger:
            with self.assertRaises(ValueError):
                self.exchange(ledger, b'{}')
        with CampaignLedger(self.journal) as ledger:
            with self.assertRaises(LedgerError):
                self.exchange(ledger, output='next')
            self.assertEqual(ledger.snapshot()['attempts'], 1)

    def test_crashed_process_after_reserve_cannot_implicitly_resume(self):
        code = "from campaign_ledger import CampaignLedger; import os,sys; l=CampaignLedger(sys.argv[1]); l.reserve_attempt(b'synthetic'); os._exit(17)"
        process = subprocess.run([sys.executable, '-c', code, str(self.journal)],
                                 cwd=Path(__file__).parent, capture_output=True, timeout=15)
        self.assertEqual(process.returncode, 17)
        with CampaignLedger(self.journal) as ledger:
            with self.assertRaises(LedgerError):
                self.exchange(ledger)
            self.assertEqual(ledger.snapshot()['reserved_nanodollars'], RESERVATION_NANODOLLARS)

    def test_artifact_drift_rejected_before_reservation_or_transport(self):
        self.artifact.write_bytes(b'drift')
        with CampaignLedger(self.journal) as ledger:
            with self.assertRaises(LedgerError):
                self.exchange(ledger)
            self.assertEqual(ledger.snapshot()['attempts'], 0)

    def test_exchange_ownership_covers_snapshot_reserve_and_failed_response(self):
        entered, release = threading.Event(), threading.Event()
        failures = []
        with CampaignLedger(self.journal) as ledger:
            original_snapshot = ledger.snapshot

            def paused_snapshot():
                result = original_snapshot()
                entered.set()
                if not release.wait(5):
                    raise TimeoutError('synthetic interleaving timed out')
                return result

            def first_exchange():
                try:
                    self.exchange(ledger, b'{}')
                except Exception as error:
                    failures.append(error)

            with patch.object(ledger, 'snapshot', side_effect=paused_snapshot):
                first = threading.Thread(target=first_exchange)
                first.start()
                try:
                    self.assertTrue(entered.wait(5))
                    with self.assertRaisesRegex(LedgerError, 'exchange already owned'):
                        self.exchange(ledger, output='concurrent')
                    self.assertEqual(original_snapshot()['attempts'], 0)
                finally:
                    release.set()
                    first.join(5)
                self.assertFalse(first.is_alive())
            self.assertEqual(len(failures), 1)
            self.assertIsInstance(failures[0], ValueError)
            with self.assertRaisesRegex(LedgerError, 'unresolved attempt'):
                self.exchange(ledger, output='after-failure')
            self.assertEqual(original_snapshot()['attempts'], 1)

    def test_real_junction_cannot_redirect_output_artifact_or_ledger_paths(self):
        import _winapi
        target, junction = self.root / 'target', self.root / 'junction'
        target.mkdir()
        (target / '.git').mkdir()
        (target / 'source').write_bytes(b'synthetic')
        _winapi.CreateJunction(str(target), str(junction))
        try:
            redirected = junction / 'source'
            group = {str(redirected): hashlib.sha256(b'synthetic').hexdigest()}
            with self.assertRaisesRegex(LedgerError, 'reparse'):
                verify_artifacts({'source': group, 'executable': group})
            with self.assertRaisesRegex(LedgerError, 'reparse'):
                CampaignLedger(junction / 'new-ledger', create_policy=Policy('synthetic', 1, 5_000_000_000, 0))
            self.assertFalse((target / 'new-ledger.lock').exists())
            with CampaignLedger(self.journal) as ledger:
                with self.assertRaisesRegex(LedgerError, 'reparse'):
                    self.exchange(ledger, output='junction/private-output')
                self.assertEqual(ledger.snapshot()['attempts'], 0)
            self.assertFalse((target / 'private-output').exists())
        finally:
            # Remove the junction itself, never recursively traverse its target.
            junction.rmdir()


if __name__ == '__main__':
    unittest.main()
