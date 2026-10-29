import hashlib
import os
from pathlib import Path
import tempfile
import threading
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

    def test_streaming_reads_full_multibuffer_content_without_read_bytes(self):
        path = Path(self.host)
        content = b'invented' * 100_000
        path.write_bytes(content)
        self.artifacts['dependency'][str(path)] = hashlib.sha256(content).hexdigest()
        with patch.object(Path, 'read_bytes', side_effect=AssertionError('whole-file allocation')):
            runner = FrozenRunner(self.roots, self.artifacts, host=self.host, assembly=self.dll)
            self.assertEqual(runner.verify(), self.artifacts)
        # Same size and timestamp, with only the last byte changed: metadata
        # cannot substitute for hashing the final partial streaming buffer.
        before = path.stat()
        path.write_bytes(content[:-1] + b'!')
        os.utime(path, ns=(before.st_atime_ns, before.st_mtime_ns))
        with self.assertRaisesRegex(LedgerError, 'hash mismatch'):
            runner.verify()

    def test_replaced_file_is_reopened_on_every_verification(self):
        runner = FrozenRunner(self.roots, self.artifacts, host=self.host, assembly=self.dll)
        original = Path(self.host)
        replacement = self.root / 'replacement'
        replacement.write_bytes(b'invented frozen byte!')
        before = original.stat()
        os.utime(replacement, ns=(before.st_atime_ns, before.st_mtime_ns))
        replacement.replace(original)
        with self.assertRaisesRegex(LedgerError, 'hash mismatch'):
            runner.verify()

    def test_concurrent_verifications_have_independent_streams(self):
        runner = FrozenRunner(self.roots, self.artifacts, host=self.host, assembly=self.dll)
        barrier = threading.Barrier(2)
        digest = hashlib.file_digest
        results, failures = [], []

        def synchronized_digest(stream, algorithm):
            barrier.wait(timeout=5)
            return digest(stream, algorithm)

        def verify():
            try:
                results.append(runner.verify())
            except BaseException as error:
                failures.append(error)

        with patch('guarded_exchange.hashlib.file_digest', side_effect=synchronized_digest):
            threads = [threading.Thread(target=verify) for _ in range(2)]
            for thread in threads:
                thread.start()
            for thread in threads:
                thread.join(10)
            self.assertFalse(any(thread.is_alive() for thread in threads))
        self.assertEqual(failures, [])
        self.assertEqual(results, [self.artifacts, self.artifacts])

    def test_post_execution_hash_rejects_drift_and_keeps_output_private(self):
        runner = FrozenRunner(self.roots, self.artifacts, host=self.host, assembly=self.dll)
        inputs = [self.root / 'input.json', self.root / 'replies.json']
        for path in inputs:
            path.write_bytes(b'{}')
        output = self.root / 'out.json'

        def fake_run(*args, **kwargs):
            Path(self.host).write_bytes(b'SECRET synthetic runtime drift')
            output.write_bytes(b'{"SECRET":"synthetic output"}')
            return type('Result', (), {'returncode': 0})()

        with patch('frozen_runner.subprocess.run', side_effect=fake_run):
            with self.assertRaisesRegex(LedgerError, '^artifact hash mismatch$'):
                runner.run('prepare', *inputs, output)

    @unittest.skipUnless(os.name == 'nt', 'Windows junction')
    def test_directory_replaced_by_junction_between_verifications_is_rejected(self):
        import _winapi
        runner = FrozenRunner(self.roots, self.artifacts, host=self.host, assembly=self.dll)
        directory = self.root / 'dependency'
        target = self.root / 'moved-dependency'
        directory.rename(target)
        _winapi.CreateJunction(str(target), str(directory))
        try:
            with self.assertRaisesRegex(LedgerError, 'reparse'):
                runner.verify()
        finally:
            directory.rmdir()
