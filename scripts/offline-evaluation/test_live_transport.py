"""All transport is fake. Never accesses provider, secrets, or private data."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

from campaign_ledger import CampaignLedger, LedgerError, Policy, RESERVATION_NANODOLLARS
from stage_campaign import StageCampaign
import live_https_worker as worker
from live_stage_campaign import LivePairedStageCampaign, system_environment


class HttpsWorkerTests(unittest.TestCase):
    def response(self, status=200, headers=None, body=b'{}'):
        result = Mock(status=status)
        result.getheaders.return_value = headers if headers is not None else [('Content-Type', 'application/json')]
        result.read.return_value = body
        return result

    def exchange(self, response):
        connection = Mock()
        connection.getresponse.return_value = response
        with patch.object(worker.http.client, 'HTTPSConnection', return_value=connection) as constructor:
            result = worker.exchange(b'{}', 'synthetic-key')
        constructor.assert_called_once()
        self.assertEqual(constructor.call_args.args, ('api.typesafe.ai', 443))
        self.assertEqual(constructor.call_args.kwargs['timeout'], 20)
        args, kwargs = connection.request.call_args
        self.assertEqual(args, ('POST', '/v1/systemone'))
        self.assertEqual(kwargs['headers']['Accept-Encoding'], 'identity')
        connection.request.assert_called_once()
        connection.close.assert_called_once()
        return result

    def test_fixed_endpoint_single_post_and_identity_encoding(self):
        with patch.dict(os.environ, {'HTTPS_PROXY': 'http://invalid', 'HTTP_PROXY': 'http://invalid'}):
            self.assertEqual(self.exchange(self.response()), b'{}')

    def test_redirect_and_provider_context_error_are_terminal_without_read(self):
        for status in (301, 302, 307, 400, 401, 413, 422, 429, 500):
            response = self.response(status=status)
            with self.subTest(status=status), self.assertRaises(ValueError):
                self.exchange(response)
            response.read.assert_not_called()

    def test_header_and_body_limits(self):
        invalid = [([('Content-Type', 'text/html')], b'{}'),
            ([('Content-Type', 'application/json'), ('Content-Length', '1048577')], b'{}'),
            ([('Content-Type', 'application/json'), ('Content-Length', '4')], b'{}'),
            ([('Content-Type', 'application/json'), ('Content-Encoding', 'gzip')], b'{}'),
            ([('Content-Type', 'application/json'), ('Content-Length', '2'), ('Transfer-Encoding', 'chunked')], b'{}'),
            ([('Content-Type', 'application/json'), ('Content-Type', 'application/json')], b'{}'),
            ([('Content-Type', 'application/json')], b'x' * (worker.MAX_BODY + 1)),
            ([('Content-Type', 'application/json')], b'')]
        for headers, body in invalid:
            with self.subTest(headers=headers), self.assertRaises(ValueError):
                self.exchange(self.response(headers=headers, body=body))

    def test_credential_control_characters_rejected_before_connection(self):
        with patch.object(worker.http.client, 'HTTPSConnection') as connect:
            for key in ('', 'x\r\nInjected: yes', 'nonascii\u00e9'):
                with self.assertRaises(ValueError):
                    worker.exchange(b'{}', key)
            connect.assert_not_called()


class ReservedChildTests(unittest.TestCase):
    def setUp(self):
        self.controller = LivePairedStageCampaign.__new__(LivePairedStageCampaign)
        self.controller.python = sys.executable
        self.controller.worker = str(Path(worker.__file__))
        self.controller.key_environment = 'SYNTHETIC_JEV_KEY'

    def test_child_explicit_environment_deadline_and_sanitized_timeout(self):
        with patch.dict(os.environ, {'SYNTHETIC_JEV_KEY': 'fake-value', 'HTTPS_PROXY': 'not-passed'}), \
                patch('live_stage_campaign.subprocess.run', side_effect=subprocess.TimeoutExpired('fake-value', 30)) as run:
            with self.assertRaisesRegex(LedgerError, '^live transport failed or timed out; reservation retained$'):
                self.controller._send_reserved(b'{}')
        run.assert_called_once()
        kwargs = run.call_args.kwargs
        self.assertEqual(kwargs['env'], {**system_environment(), 'SYNTHETIC_JEV_KEY': 'fake-value'})
        self.assertEqual(kwargs['timeout'], 30)
        self.assertEqual(run.call_args.args[0][1:3], ['-I', '-B'])
        self.assertEqual(kwargs['stderr'], subprocess.DEVNULL)

    def test_missing_named_key_does_not_spawn(self):
        with patch.dict(os.environ, {}, clear=True), patch('live_stage_campaign.subprocess.run') as run:
            with self.assertRaises(LedgerError):
                self.controller._send_reserved(b'{}')
            run.assert_not_called()

    def test_interpreter_and_executing_source_membership_required(self):
        with self.assertRaises(LedgerError):
            self.controller._verify_live_files({'source': {}, 'dependency': {}})


@unittest.skipUnless(os.name == 'nt', 'Windows durable ledger')
class LiveStageReservationTests(unittest.TestCase):
    def test_parser_failure_and_timeout_keep_full_reservation_and_stop_restart(self):
        # Exercise the new exchange transition with the real Windows ledger.
        # Fake ONLY runner/transport here; real C# parser integration is covered
        # separately by test_paired_stage_campaign.py, not claimed by this test.
        for failure in ('parser', 'timeout', 'usage', 'distribution'):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as name:
                root = Path(name)
                source = root / 'source.txt'
                source.write_bytes(b'synthetic')
                artifact = {str(source): hashlib.sha256(b'synthetic').hexdigest()}
                artifacts = {'source': artifact, 'executable': artifact}
                payload = json.dumps({'model': 'jev-1.14.0', 'questions': {'q': {'type': 'noul'}}})
                if failure == 'distribution':
                    payload = json.dumps({'model': 'jev-1.14.0', 'questions': {'q': {
                        'type': 'choice', 'criteria': {'a': 'Synthetic A', 'b': 'Synthetic B'}}}})
                plan = {'case': 'synthetic', 'plan_sha256': 'a' * 64,
                        'production': payload, 'shortlist': [payload]}
                with CampaignLedger(root / 'ledger', create_policy=Policy('synthetic', 4, 5_000_000_000, 0)) as ledger:
                    offline = StageCampaign(ledger, plan, artifacts=artifacts, private_root=root)
                    # Borrow state only for this fake-transition fixture. Public
                    # live construction still requires the frozen actual runner.
                    controller = LivePairedStageCampaign.__new__(LivePairedStageCampaign)
                    controller.__dict__.update(offline.__dict__)
                    controller._state = offline._state
                    controller._verify_runner = Mock()
                    controller._validate_response = Mock(side_effect=LedgerError('synthetic parser failure') if failure == 'parser' else None)
                    def send(raw):
                        self.assertEqual(ledger.snapshot()['attempts'], 1)
                        self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)
                        self.assertEqual(ledger.stage_history()[1]['event']['op'], 'start')
                        if failure == 'timeout':
                            raise LedgerError('synthetic timeout')
                        answer = ({'type': 'choice', 'choice': 'a', 'confidence': .5,
                                   'probabilities': {'a': .6, 'b': .389999999998}}
                                  if failure == 'distribution' else {'type': 'noul', 'noul': .95})
                        return json.dumps({'model': 'jev-1.14.0', 'answers': {'q': answer},
                            'usage': None if failure == 'usage' else {'input_tokens': 2, 'output_tokens': 1}}).encode()
                    controller._send_reserved = Mock(side_effect=send)
                    with self.assertRaises(LedgerError):
                        controller.exchange('baseline')
                    snapshot = ledger.snapshot()
                    self.assertEqual(snapshot['missing_outcomes'], 1)
                    self.assertEqual(snapshot['reserved_nanodollars'], RESERVATION_NANODOLLARS)
                    self.assertFalse(any(row.get('event', {}).get('op') == 'finish' for row in ledger.stage_history()))
                    self.assertEqual((root / 'stage-0/request.json').read_bytes(), payload.encode())
                with CampaignLedger(root / 'ledger') as ledger:
                    restarted = StageCampaign(ledger, plan, artifacts=artifacts, private_root=root)
                    controller.ledger = ledger
                    controller._state = restarted._state
                    with self.assertRaises(LedgerError):
                        controller.exchange('baseline')
                    self.assertEqual(controller._send_reserved.call_count, 1)
                    self.assertEqual(ledger.snapshot()['reserved_nanodollars'], RESERVATION_NANODOLLARS)


if __name__ == '__main__':
    unittest.main()
