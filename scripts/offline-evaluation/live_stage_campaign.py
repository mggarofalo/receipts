"""Explicit opt-in, single-case HTTPS seam; no production registration or CLI.

Use only within existing data/budget authorization. No new approval gate is added.
"""
import os
import ctypes
from pathlib import Path
import re
import subprocess
import sys

import campaign_ledger
import frozen_runner
import guarded_exchange
import paired_stage_campaign
import stage_campaign
import safe_os_diagnostics
from safe_os_diagnostics import Operation, Stage, safe_os
from campaign_ledger import LedgerError, _canonical, require_plain_absolute_path
from guarded_exchange import MODEL, MAX_RESPONSE_BYTES, validate_envelope, verify_artifacts
from paired_stage_campaign import PairedStageCampaign
from stage_campaign import digest, owner


def system_environment():
    """Windows socket providers need SystemRoot; obtain it from OS, not env."""
    if os.name != 'nt':
        return {}
    buffer = ctypes.create_unicode_buffer(32768)
    count = ctypes.windll.kernel32.GetWindowsDirectoryW(buffer, len(buffer))
    if not 0 < count < len(buffer):
        raise LedgerError('Windows directory unavailable')
    return {'SystemRoot': buffer.value}


class LivePairedStageCampaign(PairedStageCampaign):
    FORMAT = 'live-paired-stage-v1'

    def __init__(self, ledger, runner, input_path, *, case, private_root,
                 key_environment, python_executable, worker_path):
        if (not isinstance(key_environment, str) or not re.fullmatch('[A-Z][A-Z0-9_]{0,99}', key_environment)
                or key_environment.casefold() == 'systemroot'):
            raise LedgerError('explicit credential environment variable name required')
        private_input = require_plain_absolute_path(input_path)
        if any((p / '.git').exists() for p in (private_input.parent, *private_input.parents)):
            raise LedgerError('private input outside checkout required')
        self.key_environment = key_environment
        self.python = str(require_plain_absolute_path(python_executable))
        self.worker = str(require_plain_absolute_path(worker_path))
        if self.python != str(Path(sys.executable)) or self.worker != str(Path(__file__).with_name('live_https_worker.py')):
            raise LedgerError('current reviewed interpreter and adjacent worker required')
        self._transport_binding = _canonical({'python': self.python, 'worker': self.worker,
            'key_environment': key_environment, 'endpoint': 'https://api.typesafe.ai/v1/systemone',
            'system_environment': system_environment(),
            'deadline_seconds': 30, 'socket_timeout_seconds': 20})
        self._verify_live_files(runner.artifacts)
        super().__init__(ledger, runner, input_path, case=case, private_root=private_root)

    def _verify_live_files(self, artifacts):
        # Exact executing source/interpreter membership, not a revision assertion.
        sources = [__file__, self.worker, *(module.__file__ for module in (
            campaign_ledger, frozen_runner, guarded_exchange, paired_stage_campaign, stage_campaign,
            safe_os_diagnostics))]
        if (any(str(Path(p)) not in artifacts['source'] for p in sources)
                or self.python not in artifacts['dependency']):
            raise LedgerError('executing Python source and interpreter inventories required')
        verify_artifacts(artifacts)

    def _binding_extra(self):
        return {**super()._binding_extra(), 'transport': guarded_exchange._parse(self._transport_binding)}

    def _verify_runner(self):
        super()._verify_runner()
        self._verify_live_files(self.runner.artifacts)
        if _canonical({'python': self.python, 'worker': self.worker,
            'key_environment': self.key_environment, 'endpoint': 'https://api.typesafe.ai/v1/systemone',
            'system_environment': system_environment(),
            'deadline_seconds': 30, 'socket_timeout_seconds': 20}) != self._transport_binding:
            raise LedgerError('live transport binding drift')

    def _send_reserved(self, payload):
        # Called only AFTER the durable stage start and reservation. The child
        # receives the named key plus OS-resolved SystemRoot, never PATH/proxy/
        # PYTHONPATH/other secrets. Windows socket providers need SystemRoot.
        key = os.environ.get(self.key_environment)
        if not key:
            raise LedgerError('named credential unavailable; reservation retained')
        try:
            with safe_os(Stage.TRANSPORT, Operation.SPAWN):
                result = subprocess.run([self.python, '-I', '-B', self.worker, self.key_environment],
                    input=payload, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                    cwd=str(Path(self.worker).parent), env={**system_environment(), self.key_environment: key},
                    timeout=30, check=False,
                    creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        except subprocess.TimeoutExpired:
            raise LedgerError('live transport failed or timed out; reservation retained') from None
        if result.returncode != 0 or not 0 < len(result.stdout) <= MAX_RESPONSE_BYTES:
            raise LedgerError('live transport rejected response; reservation retained')
        return result.stdout

    def exchange(self, stage):
        with owner(), safe_os(Stage.EXCHANGE, Operation.DISPATCH):
            done, pending, verifier = self._state()
            if pending or self.ledger.snapshot()['missing_outcomes']:
                raise LedgerError('unfinished stage or attempt; stop and inspect')
            index = len(done)
            expected = self._expected_stage(index, verifier)
            payload = self._payload(index, verifier)
            if stage != expected:
                raise LedgerError('stage skipping/reordering/replay rejected')
            output = require_plain_absolute_path(self.root / f'stage-{index}', allow_missing_leaf=True)
            if output.exists():
                raise LedgerError('new private stage output required')
            with safe_os(Stage.START, Operation.JOURNAL_APPEND):
                self.ledger.append_stage_event({'op': 'start', 'index': index, 'request_sha256': digest(payload)})
            with safe_os(Stage.RESERVATION, Operation.JOURNAL_APPEND):
                attempt = self.ledger.reserve_attempt(payload)
            with safe_os(Stage.DIRECTORY, Operation.MKDIR):
                output.mkdir()
            self._persist(output / 'request.json', payload, stage=Stage.REQUEST)
            self._verify_runner()
            response = self._send_reserved(payload)
            self._persist(output / 'response.json', response, stage=Stage.RESPONSE)
            usage = validate_envelope(payload, response)
            # Real production/shortlist/verifier C# parsers run before success.
            self._validate_response(index, response)
            self._verify_runner()
            receipt = {'format': self.FORMAT, 'attempt': attempt, 'network_calls': 1,
                'model': MODEL, 'artifacts': self.artifacts, 'usage': usage,
                'request_sha256': digest(payload), 'response_sha256': digest(response)}
            self._persist(output / 'receipt.json', _canonical(receipt), stage=Stage.RECEIPT)
            with safe_os(Stage.OUTCOME, Operation.JOURNAL_APPEND):
                self.ledger.record_outcome(attempt, 'success')
            with safe_os(Stage.FINISH, Operation.JOURNAL_APPEND):
                self._finish(index, receipt)
            return receipt

    @staticmethod
    def _persist(path, raw, *, stage=Stage.ARTIFACT):
        with safe_os(stage, Operation.OPEN):
            stream = require_plain_absolute_path(path, allow_missing_leaf=True).open('xb')
        # Preserve the first failure if close also fails. No overwrite or retry.
        try:
            with safe_os(stage, Operation.WRITE):
                if stream.write(raw) != len(raw):
                    raise LedgerError('partial artifact write; stop and inspect')
            with safe_os(stage, Operation.FLUSH):
                stream.flush()
            with safe_os(stage, Operation.FSYNC):
                os.fsync(stream.fileno())
        except BaseException:
            try:
                stream.close()
            except OSError:
                pass
            raise
        else:
            with safe_os(stage, Operation.CLOSE):
                stream.close()
