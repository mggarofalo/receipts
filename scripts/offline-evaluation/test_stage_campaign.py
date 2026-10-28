import hashlib
import json
import os
from pathlib import Path
import tempfile
import subprocess
import sys
import threading
import unittest
from unittest.mock import patch

from campaign_ledger import CampaignLedger, LedgerError, Policy
from guarded_exchange import RecordedTransport
from stage_campaign import StageCampaign

PAYLOAD = b'{"model":"jev-1.14.0","questions":{"fit":{"type":"noul"}}}'
REPLY = b'{"model":"jev-1.14.0","answers":{"fit":{"type":"noul","noul":0.9}},"usage":{"input_tokens":2,"output_tokens":1}}'

@unittest.skipUnless(os.name == 'nt', 'Windows ledger')
class StageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        artifact = self.root / 'frozen'
        artifact.write_bytes(b'synthetic')
        group = {str(artifact): hashlib.sha256(b'synthetic').hexdigest()}
        self.artifacts = {'source': group, 'executable': group}
        self.path = self.root / 'ledger'
        with CampaignLedger(self.path, create_policy=Policy('stages', 8, 5_000_000_000, 0)):
            pass
        self.plan = {'case': 'invented-case', 'plan_sha256': 'a' * 64,
                     'production': PAYLOAD.decode(), 'shortlist': [PAYLOAD.decode(), PAYLOAD.decode()]}

    def controller(self, ledger):
        return StageCampaign(ledger, self.plan, artifacts=self.artifacts, private_root=self.root)

    def test_persistent_order_exact_plan_and_no_replay(self):
        with CampaignLedger(self.path) as ledger:
            controller = self.controller(ledger)
            with self.assertRaises(LedgerError):
                controller.exchange('shortlist:0', RecordedTransport(REPLY))
            controller.exchange('baseline', RecordedTransport(REPLY))
        with CampaignLedger(self.path) as ledger:
            controller = self.controller(ledger)
            with self.assertRaises(LedgerError):
                controller.exchange('baseline', RecordedTransport(REPLY))
            controller.exchange('shortlist:0', RecordedTransport(REPLY))
            controller.exchange('shortlist:1', RecordedTransport(REPLY))
            controller.bind_verifier(PAYLOAD, 'b' * 64)
            controller.exchange('verifier', RecordedTransport(REPLY))
            self.assertTrue(controller.complete)
            with self.assertRaises(LedgerError):
                controller.bind_verifier(None, None)
            self.assertEqual(ledger.snapshot()['attempts'], 4)
        changed = dict(self.plan, case='another-case')
        with CampaignLedger(self.path) as ledger, self.assertRaises(LedgerError):
            StageCampaign(ledger, changed, artifacts=self.artifacts, private_root=self.root)

    def test_crash_gap_after_successful_exchange_blocks_restart(self):
        with CampaignLedger(self.path) as ledger:
            controller = self.controller(ledger)
            with patch.object(controller, '_finish', side_effect=RuntimeError('crash')):
                with self.assertRaises(RuntimeError):
                    controller.exchange('baseline', RecordedTransport(REPLY))
        with CampaignLedger(self.path) as ledger:
            controller = self.controller(ledger)
            with self.assertRaisesRegex(LedgerError, 'unfinished stage'):
                controller.exchange('baseline', RecordedTransport(REPLY))
            self.assertEqual(ledger.snapshot()['attempts'], 1)

    def test_response_drift_and_failed_response_stop_progress(self):
        with CampaignLedger(self.path) as ledger:
            controller = self.controller(ledger)
            controller.exchange('baseline', RecordedTransport(REPLY))
            (self.root / 'stage-0' / 'response.json').write_bytes(b'drift')
            with self.assertRaises(LedgerError):
                controller.exchange('shortlist:0', RecordedTransport(REPLY))
            self.assertEqual(ledger.snapshot()['attempts'], 1)

    def test_bad_response_leaves_terminal_stage_across_restart(self):
        with CampaignLedger(self.path) as ledger:
            controller = self.controller(ledger)
            with self.assertRaises(LedgerError):
                controller.exchange('baseline', RecordedTransport(b'{}'))
        with CampaignLedger(self.path) as ledger:
            controller = self.controller(ledger)
            with self.assertRaisesRegex(LedgerError, 'unfinished stage'):
                controller.exchange('baseline', RecordedTransport(REPLY))
            self.assertEqual(ledger.snapshot()['attempts'], 1)

    def test_child_crash_after_exchange_success_is_terminal(self):
        config = self.root / 'config.json'
        config.write_text(json.dumps({'plan': self.plan, 'artifacts': self.artifacts}))
        code = """import json,os,sys
from campaign_ledger import CampaignLedger
from guarded_exchange import RecordedTransport
from stage_campaign import StageCampaign
from test_stage_campaign import REPLY
from pathlib import Path
root=Path(sys.argv[1]); cfg=json.loads((root/'config.json').read_text())
with CampaignLedger(root/'ledger') as ledger:
    controller=StageCampaign(ledger,cfg['plan'],artifacts=cfg['artifacts'],private_root=root)
    controller._finish=lambda *args: os._exit(17)
    controller.exchange('baseline',RecordedTransport(REPLY))
"""
        process = subprocess.run([sys.executable, '-c', code, str(self.root)],
                                 cwd=Path(__file__).parent, capture_output=True, timeout=15)
        self.assertEqual(process.returncode, 17)
        with CampaignLedger(self.path) as ledger:
            controller = self.controller(ledger)
            with self.assertRaisesRegex(LedgerError, 'unfinished stage'):
                controller.exchange('baseline', RecordedTransport(REPLY))
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 0)
            self.assertEqual(ledger.snapshot()['attempts'], 1)

    def test_verifier_cannot_bind_early_or_change(self):
        with CampaignLedger(self.path) as ledger:
            controller = self.controller(ledger)
            with self.assertRaises(LedgerError):
                controller.bind_verifier(PAYLOAD, 'b' * 64)
            for name in ('baseline', 'shortlist:0', 'shortlist:1'):
                controller.exchange(name, RecordedTransport(REPLY))
            controller.bind_verifier(None, None)
            self.assertTrue(controller.complete)
            with self.assertRaises(LedgerError):
                controller.exchange('verifier', RecordedTransport(REPLY))

    def test_concurrent_stage_owner_cannot_reserve(self):
        entered, release = threading.Event(), threading.Event()
        failures = []
        with CampaignLedger(self.path) as ledger:
            controller = self.controller(ledger)
            original = controller._finish
            def paused(*args):
                entered.set()
                release.wait(5)
                return original(*args)
            def first():
                try:
                    controller.exchange('baseline', RecordedTransport(REPLY))
                except Exception as error:
                    failures.append(error)
            with patch.object(controller, '_finish', side_effect=paused):
                thread = threading.Thread(target=first)
                thread.start()
                try:
                    self.assertTrue(entered.wait(5))
                    with self.assertRaises(LedgerError):
                        controller.exchange('baseline', RecordedTransport(REPLY))
                finally:
                    release.set()
                    thread.join(5)
            self.assertFalse(thread.is_alive())
            self.assertFalse(failures)
            self.assertEqual(ledger.snapshot()['attempts'], 1)

    def test_checkout_journal_and_lock_rejected_before_creation(self):
        checkout = self.root / 'checkout'
        checkout.mkdir()
        (checkout / '.git').write_text('synthetic-worktree')
        journal = checkout / 'private-ledger'
        with self.assertRaisesRegex(LedgerError, 'outside a checkout'):
            CampaignLedger(journal, create_policy=Policy('test', 2, 5_000_000_000, 0))
        self.assertFalse(journal.exists())
        self.assertFalse(Path(str(journal) + '.lock').exists())

    def test_artifact_drift_during_response_never_completes_stage(self):
        transport = RecordedTransport(REPLY)
        def drifting_exchange(payload):
            (self.root / 'frozen').write_bytes(b'drift during response')
            return REPLY
        with CampaignLedger(self.path) as ledger:
            controller = self.controller(ledger)
            with patch.object(transport, 'exchange', side_effect=drifting_exchange):
                with self.assertRaisesRegex(LedgerError, 'artifact hash mismatch'):
                    controller.exchange('baseline', transport)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)
            self.assertFalse(any(row.get('event', {}).get('op') == 'finish' for row in ledger.stage_history()))
        # Even restoring artifacts cannot resume the retained unfinished stage.
        (self.root / 'frozen').write_bytes(b'synthetic')
        with CampaignLedger(self.path) as ledger:
            controller = self.controller(ledger)
            with self.assertRaisesRegex(LedgerError, 'unfinished stage'):
                controller.exchange('baseline', RecordedTransport(REPLY))

    def test_public_plan_copies_and_payload_snapshots_cannot_change_bound_request(self):
        with CampaignLedger(self.path) as ledger:
            controller = self.controller(ledger)
            self.plan['production'] = PAYLOAD.decode().replace('fit', 'changed')
            copy = controller.plan
            copy['production'] = 'changed'
            copied_binding = controller.binding
            copied_binding['plan']['production'] = 'changed'
            with self.assertRaises(TypeError):
                controller.payloads[0] = 'changed'
            with self.assertRaises(AttributeError):
                controller.payloads = ['changed']
            controller.exchange('baseline', RecordedTransport(REPLY))
            self.assertEqual((self.root / 'stage-0/request.json').read_bytes(), PAYLOAD)
