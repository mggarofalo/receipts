"""Synthetic end-to-end evidence for the coordinator and real C# parsers.

No network, credentials, runtime attestation, or model-quality claim. The CLI
is built from this checkout; inputs and replies are entirely invented.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch
from uuid import uuid4

from campaign_ledger import CampaignLedger, LedgerError, Policy, RESERVATION_NANODOLLARS
from guarded_exchange import RecordedTransport
from offline_coordinator import OfflineCoordinator, UnresolvedStage


@unittest.skipUnless(os.name == 'nt', 'Windows fixed-disk ledger')
class ActualArmsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.repo = Path(__file__).resolve().parents[2]
        cls.temp = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.temp.cleanup)
        cls.root = Path(cls.temp.name)
        project = cls.repo / 'tools/Receipts.JevPairedRunner'
        cache_report = subprocess.check_output(['dotnet', 'nuget', 'locals', 'global-packages', '--list'], text=True)
        cache = cache_report.partition(': ')[2].strip()
        with (cls.root / 'build.log').open('wb') as log:
            subprocess.run(['dotnet', 'restore', str(project), '--source', cache, '-p:NuGetAudit=false'],
                           stdout=log, stderr=log, check=True)
            subprocess.run(['dotnet', 'build', str(project), '--no-restore'], stdout=log, stderr=log, check=True)
        cls.host = shutil.which('dotnet')
        cls.dll = project / 'bin/Debug/net10.0/Receipts.JevPairedRunner.dll'

    def setUp(self):
        self.work = self.root / self._testMethodName
        self.work.mkdir()
        self.ledger_path = self.work / 'ledger'
        self.input = self.work / 'input.json'
        self.input.write_text(json.dumps({'purchase': {'description': 'Purchased fresh apples', 'store': None,
            'productCode': None}, 'taxonomyRevision': 'synthetic-v1', 'taxonomy': [
                {'id': 'food', 'name': 'Food', 'definition': 'Edible goods', 'parentId': None},
                {'id': 'apple', 'name': 'Apples', 'definition': 'Fresh apples', 'parentId': 'food'},
                {'id': 'tools', 'name': 'Tools', 'definition': 'Hand tools', 'parentId': None}]}))

    def run_cs(self, command, replies):
        name = str(uuid4())
        source, result = self.work / (name + '-replies.json'), self.work / (name + '-output.json')
        source.write_text(json.dumps(replies), encoding='utf-8')
        proc = subprocess.run([self.host, str(self.dll), command, str(self.input), str(source), str(result)],
                              stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                              creationflags=subprocess.CREATE_NO_WINDOW, timeout=60)
        if proc.returncode != 0:
            raise LedgerError('actual C# command rejected response')
        return json.loads(result.read_text(encoding='utf-8'))

    @staticmethod
    def reply(payload, phase):
        request = json.loads(payload)
        answers = {}
        for key, question in request['questions'].items():
            if question['type'] == 'noul':
                value = .95
                if phase == 'shortlist':
                    value = .05 if question['instructions']['path']['categoryId'] == 'tools' else .95
                if phase == 'verification' and key.endswith('_unlisted'):
                    value = .05
                answers[key] = {'type': 'noul', 'noul': value}
            else:
                choices = list(question['criteria'])
                chosen = 'apple' if 'apple' in choices else 'food'
                answers[key] = {'type': 'choice', 'choice': chosen, 'confidence': 1,
                                'probabilities': {choice: int(choice == chosen) for choice in choices}}
        return json.dumps({'model': request['model'], 'answers': answers,
                           'usage': {'input_tokens': 2, 'output_tokens': 1}}).encode()

    def execute_case(self, coordinator, case):
        fixture = json.loads(self.input.read_text())
        fixture['purchase']['description'] = ('Purchased fresh apples' if case == 'case-a'
                                               else 'Bought an apple for a snack')
        self.input.write_text(json.dumps(fixture))
        replies = {'production': '', 'shortlist': [], 'verification': None}
        initial = self.run_cs('prepare', replies)
        self.assertEqual(initial['maximumNetworkCalls'], 0)
        def exchange(arm, phase, index, payload):
            payload_bytes = payload.encode()
            def parse(raw):
                attempts = [r for r in coordinator.ledger.stage_history() if r['kind'] == 'attempt']
                self.assertTrue((self.work / (attempts[-1]['id'] + '.response')).is_file())
                trial = {'production': replies['production'], 'shortlist': list(replies['shortlist']),
                         'verification': replies['verification']}
                if phase == 'production':
                    trial['production'] = raw.decode()
                elif phase == 'shortlist':
                    trial['shortlist'].append(raw.decode())
                else:
                    trial['verification'] = raw.decode()
                self.assertEqual(self.run_cs('validate', trial), {'validated': True, 'maximumNetworkCalls': 0})
                return raw.decode()
            return coordinator.execute([case, arm, phase, index], payload_bytes,
                RecordedTransport(self.reply(payload, phase)), parse)
        replies['production'] = exchange('baseline', 'production', 0, initial['productionPayload'])
        for i, payload in enumerate(initial['shortlistPayloads']):
            replies['shortlist'].append(exchange('candidate', 'shortlist', i, payload))
        dynamic = self.run_cs('prepare', replies)
        self.assertEqual(dynamic['planSha256'], initial['planSha256'])
        if dynamic['verificationPayload'] is not None:
            replies['verification'] = exchange('candidate', 'verification', 0, dynamic['verificationPayload'])
        return self.run_cs('replay', replies)

    def test_two_cases_use_shared_ledger_and_actual_decisions(self):
        with CampaignLedger(self.ledger_path, create_policy=Policy('synthetic-real-arms', 8,
                8 * RESERVATION_NANODOLLARS, 0)) as ledger:
            coordinator = OfflineCoordinator(ledger, self.work)
            for case in ('case-a', 'case-b'):
                result = self.execute_case(coordinator, case)
                self.assertEqual(result['production']['suggestion']['categoryId'], 'apple')
                self.assertEqual(result['candidate']['categoryId'], 'apple')
                attempts = [r['stage_id'] for r in ledger.stage_history() if r['kind'] == 'attempt']
            self.assertEqual(attempts, [[case, arm, phase, 0] for case in ('case-a', 'case-b')
                for arm, phase in (('baseline', 'production'), ('candidate', 'shortlist'),
                                   ('candidate', 'verification'))])
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 0)

    def test_invalid_actual_parser_preserves_response_and_no_resend(self):
        with CampaignLedger(self.ledger_path, create_policy=Policy('synthetic-failure', 4,
                4 * RESERVATION_NANODOLLARS, 0)) as ledger:
            coordinator = OfflineCoordinator(ledger, self.work)
            initial = self.run_cs('prepare', {'production': '', 'shortlist': [], 'verification': None})
            payload = initial['productionPayload'].encode()
            stage = ['case-a', 'baseline', 'production', 0]
            def parse(raw):
                self.run_cs('validate', {'production': raw.decode(), 'shortlist': [], 'verification': None})
            with self.assertRaises(LedgerError):
                coordinator.execute(stage, payload, RecordedTransport(b'{}'), parse)
            unused = RecordedTransport(self.reply(payload, 'production'))
            with self.assertRaises(LedgerError):
                coordinator.execute(stage, payload, unused, parse)
            self.assertFalse(unused.used)
            blocked = RecordedTransport(b'{}')
            with self.assertRaises(UnresolvedStage):
                coordinator.execute(['case-a', 'candidate', 'shortlist', 0], b'next', blocked, bytes)
            self.assertFalse(blocked.used)
            self.assertEqual(self.execute_case(coordinator, 'case-b')['candidate']['categoryId'], 'apple')
            self.assertEqual(ledger.snapshot()['attempts'], 4)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)
        with CampaignLedger(self.ledger_path) as ledger:
            unused = RecordedTransport(self.reply(payload, 'production'))
            with self.assertRaises(LedgerError):
                OfflineCoordinator(ledger, self.work).execute(stage, payload, unused, parse)
            self.assertFalse(unused.used)
            self.assertEqual(ledger.snapshot()['attempts'], 4)




    def test_missing_response_is_unknown_after_reopen_and_never_resent(self):
        with CampaignLedger(self.ledger_path, create_policy=Policy('synthetic-unknown', 4,
                4 * RESERVATION_NANODOLLARS, 0)) as ledger:
            coordinator = OfflineCoordinator(ledger, self.work)
            initial = self.run_cs('prepare', {'production': '', 'shortlist': [], 'verification': None})
            payload = initial['productionPayload'].encode()
            stage = ['case-a', 'baseline', 'production', 0]
            sent = RecordedTransport(self.reply(payload, 'production'))
            with patch.object(coordinator, '_persist', side_effect=OSError('synthetic interruption')):
                with self.assertRaises(OSError):
                    coordinator.execute(stage, payload, sent, bytes)
            self.assertTrue(sent.used)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)
        with CampaignLedger(self.ledger_path) as ledger:
            coordinator = OfflineCoordinator(ledger, self.work)
            unused = RecordedTransport(self.reply(payload, 'production'))
            with self.assertRaises(UnresolvedStage):
                coordinator.execute(stage, payload, unused, bytes)
            self.assertFalse(unused.used)
            self.assertEqual(self.execute_case(coordinator, 'case-b')['candidate']['categoryId'], 'apple')
            self.assertEqual(ledger.snapshot()['attempts'], 4)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)

    def test_shared_budget_blocks_extra_actual_case_before_consuming_reply(self):
        with CampaignLedger(self.ledger_path, create_policy=Policy('synthetic-cap', 3,
                3 * RESERVATION_NANODOLLARS, 0)) as ledger:
            coordinator = OfflineCoordinator(ledger, self.work)
            self.assertEqual(self.execute_case(coordinator, 'case-a')['candidate']['categoryId'], 'apple')
            fixture = json.loads(self.input.read_text())
            fixture['purchase']['description'] = 'Bought an apple for a snack'
            self.input.write_text(json.dumps(fixture))
            initial = self.run_cs('prepare', {'production': '', 'shortlist': [], 'verification': None})
            payload = initial['productionPayload'].encode()
            unused = RecordedTransport(self.reply(payload, 'production'))
            with self.assertRaises(LedgerError):
                coordinator.execute(['case-b', 'baseline', 'production', 0], payload, unused, bytes)
            self.assertFalse(unused.used)
            self.assertEqual(ledger.snapshot()['attempts'], 3)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 0)
