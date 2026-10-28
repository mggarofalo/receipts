import hashlib
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from campaign_ledger import LedgerError
from frozen_runner import FrozenRunner

class FrozenRunnerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.roots, self.artifacts = {}, {}
        for kind, filename in [('source', 'Program.cs'), ('executable', 'Receipts.JevPairedRunner.dll'), ('dependency', 'dotnet.exe')]:
            folder = self.root / kind
            folder.mkdir()
            path = folder / filename
            path.write_bytes(b'invented frozen bytes')
            self.roots[kind] = str(folder)
            self.artifacts[kind] = {str(path): hashlib.sha256(path.read_bytes()).hexdigest()}
        self.host = str(self.root / 'dependency/dotnet.exe')
        self.dll = str(self.root / 'executable/Receipts.JevPairedRunner.dll')

    def test_dependency_drift_or_unlisted_file_prevents_execution(self):
        runner = FrozenRunner(self.roots, self.artifacts, host=self.host, assembly=self.dll)
        with patch('frozen_runner.subprocess.run') as run:
            (self.root / 'dependency/extra.dll').write_bytes(b'extra')
            with self.assertRaises(LedgerError):
                runner.verify()
            run.assert_not_called()
            (self.root / 'dependency/extra.dll').unlink()
            Path(self.host).write_bytes(b'drift')
            with self.assertRaises(LedgerError):
                runner.verify()

    def test_explicit_host_assembly_and_complete_inventories_are_required(self):
        runner = FrozenRunner(self.roots, self.artifacts, host=self.host, assembly=self.dll)
        self.assertEqual(runner.verify(), self.artifacts)
        with self.assertRaises(LedgerError):
            FrozenRunner(self.roots, self.artifacts, host=self.dll, assembly=self.host)

    def test_run_launches_only_reviewed_absolute_host_with_no_environment(self):
        runner = FrozenRunner(self.roots, self.artifacts, host=self.host, assembly=self.dll)
        inputs = [self.root / 'input.json', self.root / 'replies.json']
        for path in inputs:
            path.write_bytes(b'{}')
        output = self.root / 'out.json'
        def fake_run(args, **kwargs):
            self.assertEqual(args, [self.host, self.dll, 'prepare', *map(str, inputs), str(output)])
            self.assertEqual(kwargs['env'], {})
            output.write_bytes(b'{"format":"synthetic"}')
            return type('Result', (), {'returncode': 0})()
        with patch('frozen_runner.subprocess.run', side_effect=fake_run) as run:
            self.assertEqual(runner.run('prepare', *inputs, output), {'format': 'synthetic'})
            self.assertEqual(run.call_count, 1)
