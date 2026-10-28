"""Synthetic integration through real published C# parsers and a private runtime.

Requires Windows and installed .NET 10 SDK. No API calls, secrets or private data.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

from campaign_ledger import CampaignLedger, LedgerError, Policy
from frozen_runner import FrozenRunner
from guarded_exchange import RecordedTransport, validate_envelope
from paired_stage_campaign import PairedStageCampaign
from stage_campaign import StageCampaign


@unittest.skipUnless(os.name == 'nt', 'Windows ledger and frozen runtime')
class ActualPairedStageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.temp.cleanup)
        cls.root = Path(cls.temp.name)
        repo = Path(__file__).resolve().parents[2]
        cls.roots = {kind: str(cls.root / kind) for kind in ('source', 'executable', 'dependency')}
        source = Path(cls.roots['source'])
        source.mkdir()
        # Retain the exact complete tracked snapshot, including current edits.
        names = subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], cwd=repo).decode().split('\0')
        for name in filter(None, names):
            target = source / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(repo / name, target)
        with (cls.root / 'publish.log').open('wb') as log:
            subprocess.run(['dotnet', 'publish', str(repo / 'tools/Receipts.JevPairedRunner'),
                            '-o', cls.roots['executable'], '--no-restore', '--nologo'], stdout=log, stderr=log, check=True)
        installed = Path(shutil.which('dotnet')).parent
        runtime = Path(cls.roots['dependency'])
        runtime.mkdir()
        shutil.copy2(installed / 'dotnet.exe', runtime)
        for relative in ('host/fxr', 'shared/Microsoft.NETCore.App'):
            version = max((installed / relative).glob('10.*'), key=lambda p: tuple(map(int, p.name.split('.'))))
            shutil.copytree(version, runtime / relative / version.name)
        cls.artifacts = {kind: {str(p): hashlib.sha256(p.read_bytes()).hexdigest()
                               for p in Path(root).rglob('*') if p.is_file()} for kind, root in cls.roots.items()}

    def setUp(self):
        self.private = self.root / self._testMethodName
        self.private.mkdir()
        self.input = self.private / 'input.json'
        self.input.write_text(json.dumps({'purchase': {'description': 'Purchased fresh apples', 'store': None, 'productCode': None},
            'taxonomyRevision': 'synthetic-v1', 'taxonomy': [{'id': 'apple', 'name': 'Apples',
            'definition': 'Fresh apples', 'parentId': None}]}))
        self.runner = FrozenRunner(self.roots, self.artifacts,
            host=str(Path(self.roots['dependency']) / 'dotnet.exe'),
            assembly=str(Path(self.roots['executable']) / 'Receipts.JevPairedRunner.dll'))
        self.ledger_path = self.private / 'ledger'
        with CampaignLedger(self.ledger_path, create_policy=Policy('actual-stage', 8, 5_000_000_000, 0)):
            pass

    def controller(self, ledger):
        return PairedStageCampaign(ledger, self.runner, self.input, case='invented-apples', private_root=self.private)

    def response(self, payload, noul=.95):
        request = json.loads(payload)
        answers = {}
        for key, question in request['questions'].items():
            if question['type'] == 'noul':
                answers[key] = {'type': 'noul', 'noul': noul}
            else:
                choices = list(question['criteria'])
                answers[key] = {'type': 'choice', 'choice': choices[0], 'confidence': 1,
                                'probabilities': {c: int(c == choices[0]) for c in choices}}
        return json.dumps({'model': request['model'], 'answers': answers,
                           'usage': {'input_tokens': 2, 'output_tokens': 1}}).encode()

    def test_actual_exports_dynamic_verifier_and_restart(self):
        with CampaignLedger(self.ledger_path) as ledger:
            controller = self.controller(ledger)
            controller.exchange('baseline', RecordedTransport(self.response(controller.payloads[0])))
        with CampaignLedger(self.ledger_path) as ledger:
            controller = self.controller(ledger)
            controller.exchange('shortlist:0', RecordedTransport(self.response(controller.payloads[1])))
            controller.bind_verifier()
            verifier = ledger.stage_history()[-1]['event']['payload']
            self.assertIsNotNone(verifier)
            controller.exchange('verifier', RecordedTransport(self.response(verifier)))
            self.assertTrue(controller.complete)
            self.assertEqual(ledger.snapshot()['attempts'], 3)
            with self.assertRaises(LedgerError):
                StageCampaign(ledger, controller.plan, artifacts=self.artifacts, private_root=self.private)

    def test_actual_abstention_is_derived_from_shortlist(self):
        with CampaignLedger(self.ledger_path) as ledger:
            controller = self.controller(ledger)
            controller.exchange('baseline', RecordedTransport(self.response(controller.payloads[0])))
            controller.exchange('shortlist:0', RecordedTransport(self.response(controller.payloads[1], 0)))
            controller.bind_verifier()
            self.assertTrue(controller.complete)
            self.assertIsNone(ledger.stage_history()[-1]['event']['payload'])
            self.assertEqual(ledger.snapshot()['attempts'], 2)

    def test_actual_parser_rejects_malformed_answer_and_input_drift_prevents_reservation(self):
        with CampaignLedger(self.ledger_path) as ledger:
            controller = self.controller(ledger)
            with self.assertRaises(LedgerError):
                controller._run('validate', {'production': '{}', 'shortlist': [], 'verification': None})
            self.assertEqual(ledger.snapshot()['attempts'], 0)
            self.input.write_text('{}')
            transport = RecordedTransport(self.response(controller.payloads[0]))
            with self.assertRaises(LedgerError):
                controller.exchange('baseline', transport)
            self.assertFalse(transport.used)
            self.assertEqual(ledger.snapshot()['attempts'], 0)

    def test_envelope_valid_actual_parser_failure_is_terminal_across_restart(self):
        with CampaignLedger(self.ledger_path) as ledger:
            controller = self.controller(ledger)
            response = b'\xef\xbb\xbf' + self.response(controller.payloads[0])
            validate_envelope(controller.payloads[0].encode(), response)
            with self.assertRaises(LedgerError):
                controller.exchange('baseline', RecordedTransport(response))
            self.assertFalse(any(row.get('event', {}).get('op') == 'finish'
                                 for row in ledger.stage_history()))
            self.assertEqual(ledger.snapshot()['attempts'], 1)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)
        with CampaignLedger(self.ledger_path) as ledger:
            controller = self.controller(ledger)
            with self.assertRaises(LedgerError):
                controller.exchange('baseline', RecordedTransport(self.response(controller.payloads[0])))

    def test_child_crash_after_actual_parser_preserves_unresolved_reservation(self):
        config = self.private / 'config.json'
        config.write_text(json.dumps({'roots': self.roots, 'artifacts': self.artifacts,
            'host': self.runner.host, 'assembly': self.runner.assembly}))
        code = """import json,os,sys
from pathlib import Path
from campaign_ledger import CampaignLedger
from frozen_runner import FrozenRunner
from paired_stage_campaign import PairedStageCampaign
from guarded_exchange import RecordedTransport
root=Path(sys.argv[1]); cfg=json.loads((root/'config.json').read_text())
runner=FrozenRunner(cfg['roots'],cfg['artifacts'],host=cfg['host'],assembly=cfg['assembly'])
with CampaignLedger(root/'ledger') as ledger:
    controller=PairedStageCampaign(ledger,runner,root/'input.json',case='invented-apples',private_root=root)
    original=controller._validate_response
    def crash(index,response):
        original(index,response)
        os._exit(19)
    controller._validate_response=crash
    controller.exchange('baseline',RecordedTransport((root/'reply.json').read_bytes()))
"""
        with CampaignLedger(self.ledger_path) as ledger:
            controller = self.controller(ledger)
            (self.private / 'reply.json').write_bytes(self.response(controller.payloads[0]))
        process = subprocess.run([sys.executable, '-c', code, str(self.private)],
            cwd=Path(__file__).parent, capture_output=True, timeout=60,
            creationflags=subprocess.CREATE_NO_WINDOW)
        self.assertEqual(process.returncode, 19)
        with CampaignLedger(self.ledger_path) as ledger:
            controller = self.controller(ledger)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)
            with self.assertRaises(LedgerError):
                controller.exchange('baseline', RecordedTransport(self.response(controller.payloads[0])))
            self.assertEqual(ledger.snapshot()['attempts'], 1)
