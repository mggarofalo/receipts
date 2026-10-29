"""Production-only cohort; explicit transport, private inputs, no CLI.

The caller freezes and audits tuning membership and labels separately before
construction. This class never reads labels and cannot establish their provenance.
"""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
from threading import Lock

from campaign_ledger import LedgerError, _canonical, _unique, require_plain_absolute_path
from guarded_exchange import RecordedTransport, validate_envelope
from cohort_live_transport import CohortLiveTransport
from live_stage_campaign import system_environment
from offline_coordinator import UnresolvedStage
from paired_cohort import CohortIdentityError, PairedCohort

_RUN_OWNER = Lock()


class BaselineCohort(PairedCohort):
    def __init__(self, coordinator, host, dll, cases, *, freeze_path, freeze_sha256, live_transport=None):
        snapshot = coordinator.ledger.snapshot()
        policy = snapshot['policy']
        if (not 1 <= policy['max_calls'] <= 12 or policy['unknown_carry_nanodollars'] < 650_000_000
                or policy['budget_nanodollars'] >= 5_000_000_000 or len(cases) > policy['max_calls']):
            raise LedgerError('baseline policy exceeds bounded campaign scope')
        self._freeze_path = require_plain_absolute_path(Path(freeze_path))
        if (any((p / '.git').exists() for p in (self._freeze_path.parent, *self._freeze_path.parents))
                or not isinstance(freeze_sha256, str) or len(freeze_sha256) != 64
                or hashlib.sha256(self._freeze_path.read_bytes()).hexdigest() != freeze_sha256):
            raise LedgerError('frozen tuning evidence unavailable or changed')
        self._freeze_sha256 = freeze_sha256
        for case in cases:
            data = case.get('input') if isinstance(case, dict) else None
            if (not isinstance(data, dict) or set(data) != {'purchase', 'taxonomyRevision', 'taxonomy'}
                    or not isinstance(data['purchase'], dict)
                    or set(data['purchase']) != {'description', 'store', 'productCode'}
                    or not isinstance(data['taxonomy'], list)
                    or any(not isinstance(node, dict) or set(node) != {'id', 'name', 'definition', 'parentId'}
                           for node in data['taxonomy'])):
                raise LedgerError('input must contain only production evidence and taxonomy')
        super().__init__(coordinator, host, dll, cases, live_transport=live_transport)
        binding = _canonical({'format': 'baseline-freeze-v1', 'path': str(self._freeze_path),
                              'sha256': freeze_sha256})
        self._freeze_binding = require_plain_absolute_path(coordinator.root / 'baseline-freeze.json', allow_missing_leaf=True)
        if self._freeze_binding.exists():
            if self._freeze_binding.read_bytes() != binding:
                raise CohortIdentityError('tuning freeze binding changed')
        else:
            if coordinator.ledger.stage_history():
                raise CohortIdentityError('cannot bind freeze after reservations')
            with self._freeze_binding.open('xb') as stream:
                stream.write(binding)
                stream.flush()
                os.fsync(stream.fileno())
        self._freeze_binding_bytes = binding
        self._check_history()

    def _build_identity(self):
        identity = super()._build_identity()
        path = Path(__file__).resolve()
        identity[str(path)] = hashlib.sha256(path.read_bytes()).hexdigest()
        helper = path.with_name('live_stage_campaign.py')
        identity[str(helper)] = hashlib.sha256(helper.read_bytes()).hexdigest()
        return identity

    def _command(self, command, case, replies):
        self._check_identity()
        with tempfile.TemporaryDirectory(dir=self.coordinator.root) as directory:
            root = Path(directory)
            source, reply, output = (root / name for name in ('input.json', 'replies.json', 'output.json'))
            source.write_bytes(_canonical(case['input']))
            reply.write_bytes(_canonical(replies))
            try:
                proc = subprocess.run([self.host, self.dll, command, str(source), str(reply), str(output)],
                    stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    cwd=str(Path(self.dll).parent), env=system_environment(),
                    creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0), timeout=60)
                if proc.returncode:
                    raise LedgerError('actual C# exchange rejected input or reply')
                return json.loads(output.read_bytes(), object_pairs_hook=_unique)
            except (OSError, subprocess.SubprocessError, ValueError) as error:
                raise LedgerError('actual C# exchange failed') from error

    def _check_history(self):
        history = self.coordinator.ledger.stage_history()
        attempts = [r for r in history if r['kind'] == 'attempt']
        expected = [[c['id'], 'baseline', 'production', 0] for c in self.cases]
        if (any(r['kind'] == 'stage' for r in history)
                or [r.get('stage_id') for r in attempts] != expected[:len(attempts)]):
            raise CohortIdentityError('unexpected or reordered campaign history')
        outcomes = {r['id']: r['status'] for r in history if r['kind'] == 'outcome'}
        if any(outcomes.get(r['id']) != 'success' for r in attempts):
            raise UnresolvedStage('unresolved or failed attempt stops the cohort')

    def run_case(self, case, responses):
        raise LedgerError('individual or candidate stages are not available')

    def _check_binding(self):
        self._check_history()
        self._check_identity()
        if (self._manifest_path.read_bytes() != self._manifest
                or _canonical({'format': 'paired-cohort-v2', 'cases': self.cases,
                               'build': self._identity, 'plans': self._plans,
                               'transport': self._transport_identity()}) != self._manifest
                or self._freeze_binding.read_bytes() != self._freeze_binding_bytes
                or hashlib.sha256(self._freeze_path.read_bytes()).hexdigest() != self._freeze_sha256):
            raise CohortIdentityError('cohort manifest changed')

    def run(self, responses):
        """Run in frozen order. Any error stops the campaign; never retry."""
        if not _RUN_OWNER.acquire(blocking=False):
            raise LedgerError('cohort already running')
        try:
            return self._run(responses)
        finally:
            _RUN_OWNER.release()

    def _run(self, responses):
        frozen = json.loads(self._manifest, object_pairs_hook=_unique)
        if not isinstance(responses, dict) or set(responses) - {c['id'] for c in frozen['cases']}:
            raise LedgerError('invalid response mapping')
        results = {}
        for index, item in enumerate(frozen['cases']):
            self._check_binding()
            transport_identity = frozen['transport']
            replies = {'production': '', 'shortlist': [], 'verification': None}
            initial = self._command('prepare', item, replies)
            if initial.get('maximumNetworkCalls') != 0 or initial.get('planSha256') != frozen['plans'][index]:
                raise CohortIdentityError('offline plan changed')
            payload = initial['productionPayload'].encode('utf-8')
            raw = responses.get(item['id'])
            if transport_identity['mode'] == 'live' and raw is not None:
                raise LedgerError('recorded responses forbidden in live mode')
            already = index < self.coordinator.ledger.snapshot()['attempts']
            if transport_identity['mode'] == 'recorded' and raw is None and not already:
                raise LedgerError('recorded response required before reservation')
            if raw is not None and type(raw) is not bytes:
                raise LedgerError('recorded response must be bytes')
            def parse(response):
                validate_envelope(payload, response)
                trial = {'production': response.decode('utf-8'), 'shortlist': [], 'verification': None}
                outcome = self._command('baseline-result', item, trial)
                if outcome.get('maximumNetworkCalls') != 0 or set(outcome) != {'production', 'maximumNetworkCalls'}:
                    raise LedgerError('actual production parser failed')
                production = outcome['production']
                if production.get('failure') is not None:
                    raise LedgerError('production failure')
                suggestion = production.get('suggestion') or {}
                category = suggestion.get('categoryId')
                return {'status': 'answered' if category is not None else 'abstained', 'categoryId': category}
            results[item['id']] = self.coordinator.execute(
                [item['id'], 'baseline', 'production', 0], payload,
                (CohortLiveTransport(transport_identity['key_environment'])
                 if transport_identity['mode'] == 'live' else RecordedTransport(raw or b'')), parse,
                pre_reserve=self._check_binding)
        return results
