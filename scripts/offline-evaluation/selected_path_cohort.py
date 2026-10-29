"""Frozen selected-path follow-ups over retained production responses.

Private evidence only. A failed or uncertain attempt permanently stops this
campaign. The caller must audit provenance and preserve the private directory.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from threading import Lock

from campaign_ledger import LedgerError, RESERVATION_NANODOLLARS, _canonical, _unique, _valid_stage_id, require_plain_absolute_path
from cohort_live_transport import CohortLiveTransport
from guarded_exchange import RecordedTransport, validate_envelope
from live_stage_campaign import system_environment
from offline_coordinator import OfflineCoordinator, UnresolvedStage
from paired_cohort import CohortIdentityError, PairedCohort

_RUN_OWNER = Lock()


class SelectedPathCohort(PairedCohort):
    def __init__(self, coordinator, host, dll, cases, *, prior_spend_nanodollars, live_transport=None):
        if (type(coordinator) is not OfflineCoordinator or not isinstance(cases, list) or len(cases) != 10
                or type(prior_spend_nanodollars) is not int or prior_spend_nanodollars < 0
                or (live_transport is not None and type(live_transport) is not CohortLiveTransport)):
            raise LedgerError('ten cases, explicit prior spend and coordinator required')
        policy = coordinator.ledger.snapshot()['policy']
        if (policy['max_calls'] != 10 or policy['unknown_carry_nanodollars'] != prior_spend_nanodollars
                or policy['budget_nanodollars'] < prior_spend_nanodollars + 10 * RESERVATION_NANODOLLARS):
            raise LedgerError('ten-call shared policy and exact prior spend required')
        ids = []
        for case in cases:
            if (not isinstance(case, dict) or set(case) != {'id', 'input', 'production'}
                    or not _valid_stage_id([case['id'], 'selected', 'path', 0])
                    or not isinstance(case['production'], str) or not case['production']):
                raise LedgerError('invalid case')
            data = case['input']
            if (not isinstance(data, dict) or set(data) != {'purchase', 'taxonomyRevision', 'taxonomy'}
                    or not isinstance(data['purchase'], dict)
                    or set(data['purchase']) != {'description', 'store', 'productCode'}
                    or not isinstance(data['taxonomy'], list)
                    or any(not isinstance(node, dict) or set(node) != {'id', 'name', 'definition', 'parentId'}
                           for node in data['taxonomy'])):
                raise LedgerError('label-free original input required')
            ids.append(case['id'])
        if len(set(ids)) != 10:
            raise LedgerError('unique cases required')
        try:
            self.cases = json.loads(_canonical(cases), object_pairs_hook=_unique)
        except (TypeError, ValueError) as error:
            raise LedgerError('invalid cohort JSON') from error
        self.coordinator = coordinator
        self.live_transport = live_transport
        self.host = str(require_plain_absolute_path(Path(shutil.which(str(host)) or str(host))))
        self.dll = str(require_plain_absolute_path(Path(dll)))
        self._prior_spend = prior_spend_nanodollars
        self._identity = self._build_identity()
        self._manifest_path = require_plain_absolute_path(coordinator.root / 'selected-path-cohort.json', allow_missing_leaf=True)
        self._stop_path = require_plain_absolute_path(coordinator.root / 'selected-path-stopped', allow_missing_leaf=True)
        if self._stop_path.exists():
            raise UnresolvedStage('campaign stopped; no retry')
        plans = []
        for case in self.cases:
            prepared = self._command('prepare-selected-path', case, None, None)
            if (prepared.get('status') != 'ready' or prepared.get('maximumNetworkCalls') != 0
                    or prepared.get('retries') != 0 or not isinstance(prepared.get('payload'), str)
                    or not isinstance(prepared.get('bindingSha256'), str) or len(prepared['bindingSha256']) != 64):
                raise LedgerError('every frozen case requires an answered production selection')
            plans.append(prepared)
        self._plans = plans
        self._manifest = self._manifest_bytes()
        if self._manifest_path.exists():
            if self._manifest_path.read_bytes() != self._manifest:
                raise CohortIdentityError('cohort, plans or build changed')
        else:
            if coordinator.ledger.stage_history():
                raise CohortIdentityError('cannot freeze after reservations')
            with self._manifest_path.open('xb') as stream:
                stream.write(self._manifest)
                stream.flush()
                os.fsync(stream.fileno())
        self._check_history()

    def _build_identity(self):
        identity = super()._build_identity()
        for name in ('selected_path_cohort.py', 'selected_path_operator.py', 'live_stage_campaign.py'):
            path = Path(__file__).resolve().with_name(name)
            identity[str(path)] = hashlib.sha256(path.read_bytes()).hexdigest()
        return identity

    def _manifest_bytes(self):
        return _canonical({'format': 'selected-path-cohort-v1', 'cases': self.cases, 'plans': self._plans,
                           'build': self._identity, 'transport': self._transport_identity(),
                           'prior_spend_nanodollars': self._prior_spend,
                           'evidence': 'description-only', 'max_attempts': 10})

    def _command(self, command, case, binding, response):
        self._check_identity()
        with tempfile.TemporaryDirectory(dir=self.coordinator.root) as directory:
            root = Path(directory)
            source, reply, output = (root / name for name in ('input.json', 'reply.json', 'output.json'))
            source.write_bytes(_canonical(case['input']))
            reply.write_bytes(_canonical({'production': case['production'], 'bindingSha256': binding, 'response': response}))
            try:
                proc = subprocess.run([self.host, self.dll, command, str(source), str(reply), str(output)],
                    stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    cwd=str(Path(self.dll).parent), env=system_environment(),
                    creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0), timeout=60)
                if proc.returncode:
                    raise LedgerError('offline exchange rejected evidence')
                return json.loads(output.read_bytes(), object_pairs_hook=_unique)
            except (OSError, subprocess.SubprocessError, ValueError) as error:
                raise LedgerError('offline exchange failed') from error

    def _check_history(self):
        history = self.coordinator.ledger.stage_history()
        attempts = [row for row in history if row['kind'] == 'attempt']
        expected = [[case['id'], 'selected', 'path', 0] for case in self.cases]
        if (any(row['kind'] == 'stage' for row in history)
                or [row.get('stage_id') for row in attempts] != expected[:len(attempts)]):
            raise CohortIdentityError('unexpected campaign history')
        outcomes = {row['id']: row['status'] for row in history if row['kind'] == 'outcome'}
        if any(outcomes.get(row['id']) != 'success' for row in attempts):
            raise UnresolvedStage('unresolved attempt stops campaign')

    def _check_binding(self):
        if self._stop_path.exists():
            raise UnresolvedStage('campaign stopped; no retry')
        self._check_history()
        self._check_identity()
        if self._manifest_path.read_bytes() != self._manifest or self._manifest_bytes() != self._manifest:
            raise CohortIdentityError('frozen cohort changed')

    def run_case(self, case, responses):
        raise LedgerError('individual dispatch unavailable')

    def run(self, responses):
        if not _RUN_OWNER.acquire(blocking=False):
            raise LedgerError('cohort already running')
        try:
            return self._run(responses)
        except Exception:
            # Durable campaign-wide stop, including errors before a reservation.
            try:
                with self._stop_path.open('xb') as stream:
                    stream.write(b'stopped\n')
                    stream.flush()
                    os.fsync(stream.fileno())
            except FileExistsError:
                pass
            raise
        finally:
            _RUN_OWNER.release()

    def _run(self, responses):
        frozen = json.loads(self._manifest, object_pairs_hook=_unique)
        if not isinstance(responses, dict) or set(responses) - {case['id'] for case in frozen['cases']}:
            raise LedgerError('invalid response mapping')
        results = {}
        for index, case in enumerate(frozen['cases']):
            self._check_binding()
            plan = self._command('prepare-selected-path', case, None, None)
            if plan != frozen['plans'][index]:
                raise CohortIdentityError('offline plan changed')
            payload = plan['payload'].encode('utf-8')
            raw = responses.get(case['id'])
            live = frozen['transport']['mode'] == 'live'
            if live and raw is not None:
                raise LedgerError('recorded response forbidden in live mode')
            if not live and raw is None and index >= self.coordinator.ledger.snapshot()['attempts']:
                raise LedgerError('recorded response required before reservation')
            if raw is not None and type(raw) is not bytes:
                raise LedgerError('recorded response must be bytes')
            def parse(response):
                validate_envelope(payload, response)
                outcome = self._command('validate-selected-path', case, plan['bindingSha256'], response.decode('utf-8'))
                if (outcome.get('validated') is not True or outcome.get('bindingSha256') != plan['bindingSha256']
                        or outcome.get('maximumNetworkCalls') != 0 or outcome.get('retries') != 0):
                    raise LedgerError('selected-path validation failed')
                return outcome['result']
            transport = (CohortLiveTransport(frozen['transport']['key_environment']) if live
                         else RecordedTransport(raw or b''))
            results[case['id']] = self.coordinator.execute([case['id'], 'selected', 'path', 0],
                payload, transport, parse, pre_reserve=self._check_binding)
        return results
