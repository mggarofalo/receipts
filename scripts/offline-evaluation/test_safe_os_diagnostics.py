"""Synthetic OS faults only; no provider, secrets, or existing private evidence."""
import hashlib
import json
import os
from pathlib import Path
import tempfile
import traceback
import unittest
import sys
from unittest.mock import Mock, patch

from campaign_ledger import CampaignLedger, LedgerError, Policy, RESERVATION_NANODOLLARS
from live_stage_campaign import LivePairedStageCampaign
from safe_os_diagnostics import Operation, SafeOSError, Stage, safe_os
from stage_campaign import StageCampaign


def fault():
    error = OSError(13, 'SECRET receipt response key', 'C:/private/SECRET.json')
    error.winerror = 32
    return error


class SafeDiagnosticsTests(unittest.TestCase):
    def assert_safe(self, error, stage, operation):
        self.assertEqual(error.diagnostic, {'kind': 'os_error', 'stage': stage,
            'operation': operation, 'errno': 13, 'winerror': 32})
        self.assertEqual(json.loads(str(error)), error.diagnostic)
        rendered = ''.join(traceback.format_exception_only(error))
        self.assertNotIn('SECRET', rendered)
        self.assertNotIn('private', rendered)
        self.assertTrue(error.__suppress_context__)

    def test_allowlisted_identifiers_and_numeric_codes_only(self):
        error = fault()
        error.errno = 'SECRET'
        error.winerror = True
        safe = SafeOSError(Stage.RECEIPT, Operation.WRITE, error)
        self.assertIsNone(safe.diagnostic['errno'])
        self.assertIsNone(safe.diagnostic['winerror'])
        with self.assertRaises(TypeError):
            with safe_os('SECRET', Operation.WRITE):
                pass
        with self.assertRaises(SafeOSError) as caught:
            with safe_os(Stage.EXCHANGE, Operation.DISPATCH):
                with safe_os(Stage.RECEIPT, Operation.FSYNC):
                    raise fault()
        self.assert_safe(caught.exception, 'receipt', 'fsync')

    def test_each_artifact_durability_boundary_and_primary_error_preserved(self):
        for operation in ('open_create_new', 'write', 'flush', 'fsync', 'close'):
            for stage in (Stage.REQUEST, Stage.RESPONSE, Stage.RECEIPT):
                with self.subTest(operation=operation, stage=stage):
                    stream = Mock()
                    stream.write.return_value = 4
                    if operation != 'open_create_new' and operation != 'fsync':
                        getattr(stream, operation).side_effect = fault()
                    # A secondary close failure must not mask the first failure.
                    if operation in ('write', 'flush', 'fsync'):
                        stream.close.side_effect = OSError(5, 'SECONDARY SECRET')
                    with patch('live_stage_campaign.require_plain_absolute_path') as path, \
                            patch('live_stage_campaign.os.fsync') as fsync:
                        path.return_value.open.return_value = stream
                        if operation == 'open_create_new':
                            path.return_value.open.side_effect = fault()
                        if operation == 'fsync':
                            fsync.side_effect = fault()
                        with self.assertRaises(SafeOSError) as caught:
                            LivePairedStageCampaign._persist(Path('unused'), b'data', stage=stage)
                    self.assert_safe(caught.exception, stage.value, operation)

    def test_create_new_never_overwrites_evidence(self):
        with tempfile.TemporaryDirectory() as name:
            path = Path(name) / 'receipt.json'
            path.write_bytes(b'original evidence')
            with self.assertRaises(SafeOSError) as caught:
                LivePairedStageCampaign._persist(path, b'replacement', stage=Stage.RECEIPT)
            self.assertEqual(caught.exception.diagnostic['operation'], 'open_create_new')
            self.assertEqual(path.read_bytes(), b'original evidence')

    def test_spawn_error_has_safe_codes_and_is_not_retried(self):
        campaign = LivePairedStageCampaign.__new__(LivePairedStageCampaign)
        campaign.python = sys.executable
        campaign.worker = 'unused'
        campaign.key_environment = 'SYNTHETIC_DIAGNOSTIC_KEY'
        with patch.dict(os.environ, {'SYNTHETIC_DIAGNOSTIC_KEY': 'SECRET'}), \
                patch('live_stage_campaign.subprocess.run', side_effect=fault()) as run:
            with self.assertRaises(SafeOSError) as caught:
                campaign._send_reserved(b'{}')
        self.assert_safe(caught.exception, 'transport', 'spawn')
        run.assert_called_once()

    def test_failing_code_getters_do_not_mask_primary_os_failure(self):
        class GetterFailure(OSError):
            @property
            def errno(self):
                raise RuntimeError('SECRET getter C:/private/SECRET.json')

            @property
            def winerror(self):
                raise KeyboardInterrupt('SECRET secondary interruption')

        original = GetterFailure('SECRET original receipt response key')
        with self.assertRaises(SafeOSError) as caught:
            with safe_os(Stage.OUTCOME, Operation.JOURNAL_APPEND):
                raise original
        self.assertIs(caught.exception.__context__, original)
        self.assertEqual(caught.exception.diagnostic, {'kind': 'os_error',
            'stage': 'outcome', 'operation': 'journal_append', 'errno': None, 'winerror': None})
        self.assertEqual(json.loads(str(caught.exception)), caught.exception.diagnostic)
        self.assertNotIn('SECRET', str(caught.exception))
        self.assertTrue(caught.exception.__suppress_context__)

    def test_failing_serializer_keeps_primary_failure_and_structured_diagnostic(self):
        original = fault()
        for secondary in (RuntimeError('SECRET serializer'), KeyboardInterrupt('SECRET')):
            with self.subTest(secondary=type(secondary).__name__), \
                    patch('safe_os_diagnostics.json.dumps', side_effect=secondary):
                with self.assertRaises(SafeOSError) as caught:
                    with safe_os(Stage.RECEIPT, Operation.FSYNC):
                        raise original
            self.assertIs(caught.exception.__context__, original)
            self.assertEqual(caught.exception.diagnostic, {'kind': 'os_error',
                'stage': 'receipt', 'operation': 'fsync', 'errno': 13, 'winerror': 32})
            self.assertEqual(str(caught.exception), 'terminal OS error; safe diagnostic available')
            self.assertNotIn('SECRET', ''.join(traceback.format_exception_only(caught.exception)))
            self.assertTrue(caught.exception.__suppress_context__)

    def test_numeric_codes_are_bounded_before_serialization(self):
        original = fault()
        for value, expected in ((-(2 ** 31), -(2 ** 31)), (2 ** 32 - 1, 2 ** 32 - 1),
                                (-(2 ** 31) - 1, None), (2 ** 32, None), (10 ** 5000, None)):
            original.errno = value
            original.winerror = value
            with self.assertRaises(SafeOSError) as caught:
                with safe_os(Stage.RECEIPT, Operation.WRITE):
                    raise original
            self.assertIs(caught.exception.__context__, original)
            self.assertEqual(caught.exception.diagnostic['errno'], expected)
            self.assertEqual(caught.exception.diagnostic['winerror'], expected)
            self.assertEqual(json.loads(str(caught.exception)), caught.exception.diagnostic)
            self.assertLess(len(str(caught.exception)), 200)


@unittest.skipUnless(os.name == 'nt', 'Windows durable ledger')
class CampaignDiagnosticsTests(SafeDiagnosticsTests):
    def test_post_validation_faults_are_terminal_and_keep_full_reservation(self):
        # Fake runner/transport only. Real ledger and artifact durability code run.
        # Actual C# validation remains covered by test_live_frozen_campaign.py.
        for boundary in ('receipt', 'outcome', 'stage_finish'):
            for operation in ('write', 'fsync'):
                with self.subTest(boundary=boundary, operation=operation), tempfile.TemporaryDirectory() as name:
                    root = Path(name)
                    source = root / 'source.txt'
                    source.write_bytes(b'synthetic')
                    artifact = {str(source): hashlib.sha256(b'synthetic').hexdigest()}
                    artifacts = {'source': artifact, 'executable': artifact}
                    payload = json.dumps({'model': 'jev-1.14.0', 'questions': {'q': {'type': 'noul'}}})
                    plan = {'case': 'synthetic', 'plan_sha256': 'a' * 64,
                            'production': payload, 'shortlist': [payload]}
                    with CampaignLedger(root / 'ledger', create_policy=Policy('synthetic', 4, 5_000_000_000, 0)) as ledger:
                        offline = StageCampaign(ledger, plan, artifacts=artifacts, private_root=root)
                        campaign = LivePairedStageCampaign.__new__(LivePairedStageCampaign)
                        campaign.__dict__.update(offline.__dict__)
                        campaign._state = offline._state
                        campaign._verify_runner = Mock()
                        campaign._validate_response = Mock()
                        campaign._send_reserved = Mock(return_value=json.dumps({'model': 'jev-1.14.0',
                            'answers': {'q': {'type': 'noul', 'noul': .95}},
                            'usage': {'input_tokens': 2, 'output_tokens': 1}}).encode())
                        persist = campaign._persist
                        outcome = ledger.record_outcome
                        finish = campaign._finish

                        def fail_journal(call, *args):
                            if operation == 'fsync':
                                with patch('campaign_ledger.os.fsync', side_effect=fault()):
                                    return call(*args)
                            with patch.object(ledger, '_journal', wraps=ledger._journal) as journal:
                                journal.write.side_effect = fault()
                                return call(*args)

                        def persist_fault(path, raw, *, stage):
                            if stage is not Stage.RECEIPT:
                                return persist(path, raw, stage=stage)
                            if operation == 'fsync':
                                with patch('live_stage_campaign.os.fsync', side_effect=fault()):
                                    return persist(path, raw, stage=stage)
                            with patch('pathlib.Path.open') as opening:
                                opening.return_value.write.side_effect = fault()
                                return persist(path, raw, stage=stage)

                        if boundary == 'receipt':
                            campaign._persist = persist_fault
                        elif boundary == 'outcome':
                            ledger.record_outcome = lambda *args: fail_journal(outcome, *args)
                        else:
                            campaign._finish = lambda *args: fail_journal(finish, *args)
                        with self.assertRaises(SafeOSError) as caught:
                            campaign.exchange('baseline')
                        campaign._validate_response.assert_called_once()
                        self.assert_safe(caught.exception, boundary,
                            operation if boundary == 'receipt' else 'journal_append')
                        campaign._send_reserved.assert_called_once()
                        request = (root / 'stage-0/request.json').read_bytes()
                        response = (root / 'stage-0/response.json').read_bytes()
                    with CampaignLedger(root / 'ledger') as reopened:
                        snapshot = reopened.snapshot()
                        self.assertEqual(snapshot['attempts'], 1)
                        self.assertEqual(snapshot['reserved_nanodollars'], RESERVATION_NANODOLLARS)
                        if boundary == 'receipt' or boundary == 'outcome' and operation == 'write':
                            self.assertEqual(snapshot['missing_outcomes'], 1)
                        restarted = StageCampaign(reopened, plan, artifacts=artifacts, private_root=root)
                        campaign.ledger = reopened
                        campaign._state = restarted._state
                        with self.assertRaises(LedgerError):
                            campaign.exchange('baseline')
                        campaign._send_reserved.assert_called_once()
                    self.assertEqual((root / 'stage-0/request.json').read_bytes(), request)
                    self.assertEqual((root / 'stage-0/response.json').read_bytes(), response)


if __name__ == '__main__':
    unittest.main()
