"""Public live-controller construction, real C# runner, fake transport only.

Copies all installed CPython stdlib sources/resources and native runtime files;
zip + ._pth isolate imports without increasing the ledger inventory past 1 MiB.
No site packages, network, credentials, or private data are used.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import ssl
import subprocess
import sys
import unittest
from unittest.mock import patch
import zipfile

import test_paired_stage_campaign as paired_tests


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def freeze_python(destination, source_root):
    installed = Path(sys.base_prefix)
    destination.mkdir()
    version = f'python{sys.version_info.major}{sys.version_info.minor}'
    # Entire root file set (interpreter, Python DLLs, VC runtime, license/build).
    # Entire DLLs tree includes _ssl/_hashlib and bundled OpenSSL DLLs.
    native_manifest = {}
    for path in installed.iterdir():
        if path.is_file():
            shutil.copy2(path, destination / path.name)
            native_manifest[path.name] = sha(path)
    shutil.copytree(installed / 'DLLs', destination / 'DLLs')
    for path in (installed / 'DLLs').rglob('*'):
        if path.is_file():
            native_manifest[path.relative_to(installed).as_posix()] = sha(path)
    stdlib = installed / 'Lib'
    standard_manifest = {}
    with zipfile.ZipFile(destination / (version + '.zip'), 'x', zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(stdlib.rglob('*')):
            relative = path.relative_to(stdlib)
            if not path.is_file() or 'site-packages' in relative.parts or '__pycache__' in relative.parts:
                continue
            # Pure stdlib resources are preserved too, not only imported .py.
            standard_manifest[relative.as_posix()] = sha(path)
            archive.write(path, relative.as_posix())
    if not standard_manifest or not any('_ssl' in name for name in native_manifest):
        raise AssertionError('stdlib/native TLS closure incomplete')
    (destination / (version + '._pth')).write_text(
        version + '.zip\nDLLs\n' + str(source_root) + '\n', encoding='utf-8')
    # Auditable per-file source manifest inside the hashed runtime inventory.
    manifest = {'origin': str(installed), 'stdlib': standard_manifest, 'native': native_manifest,
        'excluded': ['Lib/site-packages (unused third-party packages)', '__pycache__ (bytecode caches)'],
        'external_trust': ['Windows system DLLs/kernel', 'Windows root certificate stores']}
    (destination / 'closure-manifest.json').write_text(json.dumps(manifest, sort_keys=True))
    # Check every copied native file and every archived stdlib member explicitly.
    for name, expected in native_manifest.items():
        assert sha(destination / name) == expected
    with zipfile.ZipFile(destination / (version + '.zip')) as archive:
        assert set(archive.namelist()) == set(standard_manifest)
        assert all(hashlib.sha256(archive.read(name)).hexdigest() == expected
                   for name, expected in standard_manifest.items())
    return destination / 'python.exe'


def frozen_case(config_path, scenario):
    from campaign_ledger import CampaignLedger, LedgerError, Policy, RESERVATION_NANODOLLARS
    from frozen_runner import FrozenRunner
    from live_stage_campaign import LivePairedStageCampaign
    import live_stage_campaign
    cfg = json.loads(Path(config_path).read_text())
    root = Path(config_path).parent
    runtime = Path(cfg['roots']['dependency'])
    source = Path(cfg['roots']['source'])
    # ._pth must exclude registry, user/site packages and ambient Python paths.
    assert sys.flags.isolated and sys.flags.dont_write_bytecode
    assert sys.executable == str(runtime / 'python' / 'python.exe')
    assert all(Path(p).is_relative_to(runtime) or Path(p).is_relative_to(source) for p in sys.path)
    context = ssl.create_default_context()  # no network; verifies bundled _ssl loads
    assert context.verify_mode == ssl.CERT_REQUIRED and context.check_hostname
    for module in tuple(sys.modules.values()):
        location = getattr(module, '__file__', None)
        if location:
            assert Path(location).is_relative_to(runtime) or Path(location).is_relative_to(source), location
    runner = FrozenRunner(cfg['roots'], cfg['artifacts'], host=cfg['host'], assembly=cfg['assembly'])
    input_path = root / 'input.json'
    input_path.write_text(json.dumps({'purchase': {'description': 'Purchased fresh apples',
        'store': None, 'productCode': None}, 'taxonomyRevision': 'synthetic-v1',
        'taxonomy': [{'id': 'apple', 'name': 'Apples', 'definition': 'Fresh apples', 'parentId': None}]}))
    ledger_path = root / 'ledger'
    def controller(ledger):
        return LivePairedStageCampaign(ledger, runner, input_path, case='synthetic-apples', private_root=root,
            key_environment='NEVER_READ_SYNTHETIC_KEY', python_executable=sys.executable,
            worker_path=str(Path(live_stage_campaign.__file__).with_name('live_https_worker.py')))
    def response(payload):
        # This is the ONLY replacement: the transport send boundary. The public
        # constructor, all inventories, ledger, C# prepare/validate and reducers run.
        request = json.loads(payload)
        answers = {}
        for name, question in request['questions'].items():
            if question['type'] == 'noul':
                answers[name] = {'type': 'noul', 'noul': .95}
            else:
                choices = list(question['criteria'])
                answers[name] = {'type': 'choice', 'choice': choices[0], 'confidence': 1,
                    'probabilities': {c: int(c == choices[0]) for c in choices}}
        raw = json.dumps({'model': request['model'], 'answers': answers,
            'usage': {'input_tokens': 2, 'output_tokens': 1}}).encode()
        return b'\xef\xbb\xbf' + raw if scenario == 'reject' else raw
    with CampaignLedger(ledger_path, create_policy=Policy('synthetic-live', 8, 5_000_000_000, 0)) as ledger:
        campaign = controller(ledger)
        with patch.object(LivePairedStageCampaign, '_send_reserved', side_effect=response) as transport:
            if scenario == 'reject':
                try:
                    campaign.exchange('baseline')
                except LedgerError:
                    pass
                else:
                    raise AssertionError('actual C# parser accepted UTF-8 BOM')
                assert ledger.snapshot()['missing_outcomes'] == 1
                assert ledger.snapshot()['reserved_nanodollars'] == RESERVATION_NANODOLLARS
                assert not any(r.get('event', {}).get('op') == 'finish' for r in ledger.stage_history())
            else:
                campaign.exchange('baseline')
            assert transport.call_count == 1
    with CampaignLedger(ledger_path) as ledger:
        campaign = controller(ledger)  # actual public reopen validates binding
        with patch.object(LivePairedStageCampaign, '_send_reserved', side_effect=response) as transport:
            if scenario == 'reject':
                try:
                    campaign.exchange('baseline')
                except LedgerError:
                    pass
                else:
                    raise AssertionError('terminal stage resumed')
                transport.assert_not_called()
                assert ledger.snapshot()['missing_outcomes'] == 1
            else:
                for index in range(len(campaign.plan['shortlist'])):
                    campaign.exchange(f'shortlist:{index}')
                campaign.bind_verifier()
                assert ledger.stage_history()[-1]['event']['payload'] is not None
                campaign.exchange('verifier')
                assert campaign.complete
                assert ledger.snapshot()['attempts'] == 3
                assert ledger.snapshot()['missing_outcomes'] == 0
    print(json.dumps({'scenario': scenario, 'actual_csharp': True, 'network_calls': 0,
                      'ssl': ssl.OPENSSL_VERSION, 'python_inventory_files': len(cfg['artifacts']['dependency'])}))


@unittest.skipUnless(os.name == 'nt', 'Windows ledger and frozen runtime')
class FrozenLiveCampaignTests(unittest.TestCase):
    FROZEN_CASE_SCRIPT = Path(__file__).name
    @classmethod
    def setUpClass(cls):
        paired_tests.ActualPairedStageTests.setUpClass.__func__(cls)
        cls.scripts = Path(cls.roots['source']) / 'scripts/offline-evaluation'
        cls.python = freeze_python(Path(cls.roots['dependency']) / 'python', cls.scripts)
        cls.artifacts['dependency'] = {str(p): sha(p) for p in Path(cls.roots['dependency']).rglob('*') if p.is_file()}

    def run_case(self, scenario):
        private = self.root / scenario
        private.mkdir()
        config = private / 'config.json'
        config.write_text(json.dumps({'roots': self.roots, 'artifacts': self.artifacts,
            'host': str(Path(self.roots['dependency']) / 'dotnet.exe'),
            'assembly': str(Path(self.roots['executable']) / 'Receipts.JevPairedRunner.dll')}))
        process = subprocess.run([str(self.python), '-I', '-B', str(self.scripts / self.FROZEN_CASE_SCRIPT),
            '--frozen-case', str(config), scenario], env={'SystemRoot': os.environ['SystemRoot']}, capture_output=True,
            # Full directory membership + byte hashes are intentionally checked
            # at every gate. Allow slow disks/antivirus a synthetic-test budget;
            # the production HTTPS child's 30-second deadline is unchanged.
            timeout=600, creationflags=subprocess.CREATE_NO_WINDOW)
        self.assertEqual(process.returncode, 0, process.stderr.decode(errors='replace')[-6000:])
        report = json.loads(process.stdout)
        self.assertTrue(report['actual_csharp'])
        self.assertEqual(report['network_calls'], 0)
        self.assertGreater(report['python_inventory_files'], 50)

    def test_public_construction_actual_parsers_full_progression_and_reopen(self):
        self.run_case('success')

    def test_actual_parser_rejection_is_terminal_after_public_reopen(self):
        self.run_case('reject')


if __name__ == '__main__':
    if len(sys.argv) == 4 and sys.argv[1] == '--frozen-case':
        frozen_case(sys.argv[2], sys.argv[3])
    else:
        unittest.main()
