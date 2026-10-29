"""Synthetic-only local benchmark; accepts no paths, payloads or credentials.

Compare the previous whole-file hash against streaming through the unchanged
FrozenRunner membership/ancestry checks. No subprocesses or network operations.
"""
import cProfile
import hashlib
import io
from pathlib import Path
import pstats
import statistics
import tempfile
import time
from unittest.mock import patch

from frozen_runner import FrozenRunner


def whole_file_digest(stream, algorithm):
    """Previous verify_artifacts hash, retained only as benchmark baseline."""
    assert algorithm == 'sha256'
    return hashlib.sha256(stream.read())


def main():
    native_digest = hashlib.file_digest
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        roots, artifacts = {}, {}
        for kind, filename in [('source', 'Program.cs'),
                               ('executable', 'Receipts.JevPairedRunner.dll'),
                               ('dependency', 'dotnet.exe')]:
            folder = root / kind
            folder.mkdir()
            roots[kind], artifacts[kind] = str(folder), {}
            for index in range(80):
                path = folder / (filename if index == 0 else f'{index}.bin')
                # 237 one-MiB files and three 64-MiB files, entirely invented.
                data = bytes([index]) * (64 if index == 0 else 1) * 1024 * 1024
                path.write_bytes(data)
                artifacts[kind][str(path)] = hashlib.sha256(data).hexdigest()
        runner = FrozenRunner(roots, artifacts, host=root / 'dependency/dotnet.exe',
                              assembly=root / 'executable/Receipts.JevPairedRunner.dll')
        samples = {'whole_file': [], 'streaming': []}
        # Alternate order to reduce warm-cache/order bias. Each run still hashes
        # the complete 429 MiB; no file identity or content checks are disabled.
        for iteration in range(6):
            order = list(samples) if iteration % 2 == 0 else list(reversed(samples))
            for mode in order:
                implementation = whole_file_digest if mode == 'whole_file' else native_digest
                with patch('guarded_exchange.hashlib.file_digest', implementation):
                    started = time.perf_counter()
                    assert runner.verify() == artifacts
                    samples[mode].append(time.perf_counter() - started)
        for mode, timings in samples.items():
            print(f'{mode}: median_seconds={statistics.median(timings):.6f}; runs={len(timings)}')
        print(f'speedup={statistics.median(samples["whole_file"]) / statistics.median(samples["streaming"]):.3f}x')
        for mode, implementation in [('whole_file', whole_file_digest), ('streaming', native_digest)]:
            profile = cProfile.Profile()
            with patch('guarded_exchange.hashlib.file_digest', implementation):
                profile.runcall(runner.verify)
            summary = io.StringIO()
            pstats.Stats(profile, stream=summary).strip_dirs().sort_stats('cumtime').print_stats(12)
            print(f'{mode} profile:\n{summary.getvalue()}')


if __name__ == '__main__':
    main()
