"""Explicit live cohort transport. No CLI and no network until exchange is called.

The coordinator must reserve the named stage before invoking this transport.
The caller supplies only the name of an environment variable; the secret is
passed to the isolated worker, never written to the ledger or diagnostics.
"""
import os
from pathlib import Path
import re
import subprocess
import sys

from campaign_ledger import LedgerError
from guarded_exchange import MAX_RESPONSE_BYTES
from live_stage_campaign import system_environment

_WORKER_KEY = "COHORT_WORKER_KEY"


class CohortLiveTransport:
    def __init__(self, key_environment):
        if (not isinstance(key_environment, str)
                or not re.fullmatch('[A-Z][A-Z0-9_]{0,99}', key_environment)
                or key_environment.casefold() == 'systemroot'):
            raise LedgerError('explicit credential environment variable name required')
        self.key_environment = key_environment

    @property
    def identity(self):
        return {'mode': 'live', 'key_environment': self.key_environment,
                'endpoint': 'https://api.typesafe.ai/v1/systemone'}

    def exchange(self, payload):
        key = os.environ.get(self.key_environment)
        if not key:
            raise LedgerError('named credential unavailable; reservation retained')
        worker = Path(__file__).with_name('live_https_worker.py')
        try:
            result = subprocess.run([sys.executable, '-I', '-B', str(worker), _WORKER_KEY],
                input=payload, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                cwd=str(worker.parent), env={**system_environment(), _WORKER_KEY: key},
                timeout=30, check=False, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        except (OSError, subprocess.SubprocessError):
            raise LedgerError('live transport failed; reservation retained') from None
        if result.returncode != 0 or not 0 < len(result.stdout) <= MAX_RESPONSE_BYTES:
            raise LedgerError('live transport failed; reservation retained')
        return result.stdout
