"""Label-blind, interleaved twelve-case holdout with one durable ledger.

The caller owns the private root, freezes membership externally, and keeps labels
separate. This controller neither reads labels nor evaluates prediction quality.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile
from threading import Lock

from baseline_cohort import BaselineCohort
from selected_path_cohort import SelectedPathCohort
from campaign_ledger import LedgerError, RESERVATION_NANODOLLARS, _canonical, _unique, _valid_stage_id, require_plain_absolute_path
from cohort_live_transport import CohortLiveTransport
from guarded_exchange import RecordedTransport, validate_envelope
from offline_coordinator import OfflineCoordinator, UnresolvedStage
from paired_cohort import CohortIdentityError, PairedCohort

_OWNER = Lock()
_EMPTY = {'production': '', 'shortlist': [], 'verification': None}


class HoldoutCohort(BaselineCohort):
    def __init__(self, coordinator, host, dll, cases, *, prior_spend_nanodollars, live_transport=None):
        if (type(coordinator) is not OfflineCoordinator or not isinstance(cases, list) or len(cases) != 12
                or type(prior_spend_nanodollars) is not int or prior_spend_nanodollars < 0
                or (live_transport is not None and type(live_transport) is not CohortLiveTransport)):
            raise LedgerError('twelve cases, coordinator and prior spend required')
        policy = coordinator.ledger.snapshot()['policy']
        if (policy['max_calls'] != 24 or policy['unknown_carry_nanodollars'] != prior_spend_nanodollars
                or policy['budget_nanodollars'] < prior_spend_nanodollars + 24 * RESERVATION_NANODOLLARS):
            raise LedgerError('shared 24-call policy and exact prior spend required')
        ids = []
        for case in cases:
            if (not isinstance(case, dict) or set(case) != {'id', 'input'}
                    or not _valid_stage_id([case['id'], 'baseline', 'production', 0])):
                raise LedgerError('invalid case')
            data = case['input']
            if (not isinstance(data, dict) or set(data) != {'purchase', 'taxonomyRevision', 'taxonomy'}
                    or not isinstance(data['purchase'], dict)
                    or set(data['purchase']) != {'description', 'store', 'productCode'}
                    or not isinstance(data['taxonomy'], list)
                    or any(not isinstance(node, dict) or set(node) != {'id', 'name', 'definition', 'parentId'}
                           for node in data['taxonomy'])):
                raise LedgerError('label-free production input required')
            ids.append(case['id'])
        if len(set(ids)) != 12:
            raise LedgerError('unique cases required')
        try:
            self.cases = json.loads(_canonical(cases), object_pairs_hook=_unique)
        except (TypeError, ValueError) as error:
            raise LedgerError('invalid cases') from error
        self.coordinator = coordinator
        self.live_transport = live_transport
        self.host = str(require_plain_absolute_path(Path(shutil.which(str(host)) or str(host))))
        self.dll = str(require_plain_absolute_path(Path(dll)))
        self._identity = self._build_identity()
        self._stop = require_plain_absolute_path(coordinator.root / 'holdout-stopped', allow_missing_leaf=True)
        if self._stop.exists():
            raise UnresolvedStage('campaign stopped; no retry')
        self._manifest_path = require_plain_absolute_path(coordinator.root / 'holdout-manifest.json', allow_missing_leaf=True)
        plans = []
        for case in self.cases:
            plan = self._command('prepare', case, _EMPTY)
            if (plan.get('maximumNetworkCalls') != 0 or not isinstance(plan.get('planSha256'), str)
                    or len(plan['planSha256']) != 64 or not isinstance(plan.get('productionPayload'), str)):
                raise LedgerError('invalid baseline plan')
            plans.append({'planSha256': plan['planSha256'], 'productionPayload': plan['productionPayload']})
        self._plans = plans
        self._manifest = self._manifest_bytes()
        if self._manifest_path.exists():
            if self._manifest_path.read_bytes() != self._manifest:
                raise CohortIdentityError('holdout manifest changed')
        else:
            if coordinator.ledger.stage_history():
                raise CohortIdentityError('cannot freeze after reservation')
            self._write_once(self._manifest_path, self._manifest)
        self._check_history()

    def _build_identity(self):
        # Same managed-output and host change detector as the paired runner;
        # it is not attestation of the runtime or the build.
        identity = PairedCohort._build_identity(self)
        for name in ('holdout_cohort.py', 'baseline_cohort.py', 'selected_path_cohort.py'):
            path = Path(__file__).resolve().with_name(name)
            identity[str(path)] = hashlib.sha256(path.read_bytes()).hexdigest()
        return identity

    def _ledger_identity(self):
        # The held journal and the named path must still identify the same file.
        # File identity distinguishes a newly created same-policy ledger even at
        # the original path. External rollback remains outside this mechanism.
        ledger = self.coordinator.ledger
        try:
            path = require_plain_absolute_path(ledger.path)
            named = os.stat(path)
            opened = os.fstat(ledger._journal.fileno())
            if (not named.st_ino or (named.st_dev, named.st_ino) != (opened.st_dev, opened.st_ino)):
                raise CohortIdentityError('ledger file changed')
            return {'path': str(path), 'device': named.st_dev, 'file_id': named.st_ino}
        except (OSError, AttributeError, ValueError) as error:
            raise CohortIdentityError('ledger identity unavailable') from error

    def _manifest_bytes(self):
        return _canonical({'format': 'holdout-v2', 'cases': self.cases, 'plans': self._plans,
                           'build': self._identity, 'transport': self._transport_identity(),
                           'ledger': self._ledger_identity(),
                           'policy': self.coordinator.ledger.snapshot()['policy']})

    @staticmethod
    def _write_once(path, data):
        with path.open('xb') as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())

    def _check_history(self):
        history = self.coordinator.ledger.stage_history()
        attempts = [row for row in history if row['kind'] == 'attempt']
        if any(row['kind'] == 'stage' or (row['kind'] == 'attempt' and 'stage_id' not in row) for row in history):
            raise CohortIdentityError('unexpected ledger history')
        outcomes = {row['id']: row['status'] for row in history if row['kind'] == 'outcome'}
        if any(outcomes.get(row['id']) != 'success' for row in attempts):
            raise UnresolvedStage('unresolved attempt stops campaign')

    def _check_binding(self):
        if self._stop.exists():
            raise UnresolvedStage('campaign stopped; no retry')
        self._check_history()
        self._check_identity()
        if self._manifest_path.read_bytes() != self._manifest or self._manifest_bytes() != self._manifest:
            raise CohortIdentityError('holdout manifest changed')

    def _exchange(self, stage, payload, responses, parse, expected):
        self._check_binding()
        attempts = [r['stage_id'] for r in self.coordinator.ledger.stage_history() if r['kind'] == 'attempt']
        expected.append(stage)
        if attempts[:len(expected)] != expected[:len(attempts)] or len(attempts) > 24:
            raise CohortIdentityError('unexpected stage order')
        raw = responses.get(tuple(stage))
        if self.live_transport is not None and raw is not None:
            raise LedgerError('recorded responses forbidden in live mode')
        if self.live_transport is None and raw is None and len(attempts) < len(expected):
            raise LedgerError('recorded response required')
        if raw is not None and type(raw) is not bytes:
            raise LedgerError('recorded response must be bytes')
        transport = self.live_transport or RecordedTransport(raw or b'')
        return self.coordinator.execute(stage, payload, transport, parse, pre_reserve=self._check_binding)

    def run_case(self, case, responses):
        raise LedgerError('individual dispatch unavailable')

    def run(self, responses):
        if not _OWNER.acquire(blocking=False):
            raise LedgerError('campaign already running')
        try:
            return self._run(responses)
        except Exception:
            try:
                self._write_once(self._stop, b'stopped\n')
            except FileExistsError:
                pass
            raise
        finally:
            _OWNER.release()

    def _run(self, responses):
        if not isinstance(responses, dict):
            raise LedgerError('responses must be a stage mapping')
        allowed = {tuple([case['id'], arm, phase, 0]) for case in self.cases
                   for arm, phase in (('baseline', 'production'), ('selected', 'path'))}
        if set(responses) - allowed:
            raise LedgerError('unexpected response stage')
        results, expected = {}, []
        for index, case in enumerate(self.cases):
            plan = self._command('prepare', case, _EMPTY)
            frozen = self._plans[index]
            if (plan.get('maximumNetworkCalls') != 0 or plan.get('planSha256') != frozen['planSha256']
                    or plan.get('productionPayload') != frozen['productionPayload']):
                raise CohortIdentityError('baseline plan changed')
            payload = frozen['productionPayload'].encode('utf-8')
            def parse_baseline(raw):
                validate_envelope(payload, raw)
                outcome = self._command('baseline-result', case,
                    {'production': raw.decode('utf-8'), 'shortlist': [], 'verification': None})
                if outcome.get('maximumNetworkCalls') != 0 or set(outcome) != {'production', 'maximumNetworkCalls'}:
                    raise LedgerError('baseline parser failed')
                production = outcome['production']
                if production.get('failure') is not None:
                    raise LedgerError('baseline failure')
                category = (production.get('suggestion') or {}).get('categoryId')
                return raw.decode('utf-8'), {'status': 'answered' if category is not None else 'abstained', 'categoryId': category}
            raw, baseline = self._exchange([case['id'], 'baseline', 'production', 0], payload,
                                           responses, parse_baseline, expected)
            if baseline['categoryId'] is None:
                results[case['id']] = {'baseline': baseline, 'selected': dict(baseline)}
                continue
            selected_case = {**case, 'production': raw}
            dynamic = SelectedPathCohort._command(self, 'prepare-selected-path', selected_case, None, None)
            if (dynamic.get('status') != 'ready' or dynamic.get('maximumNetworkCalls') != 0
                    or dynamic.get('retries') != 0 or not isinstance(dynamic.get('payload'), str)
                    or not isinstance(dynamic.get('bindingSha256'), str) or len(dynamic['bindingSha256']) != 64
                    or dynamic.get('categoryId') != baseline['categoryId']):
                raise LedgerError('invalid selected path plan')
            dynamic_path = require_plain_absolute_path(self.coordinator.root / f'holdout-plan-{index:02d}.json', allow_missing_leaf=True)
            frozen_dynamic = _canonical(dynamic)
            if dynamic_path.exists():
                if dynamic_path.read_bytes() != frozen_dynamic:
                    raise CohortIdentityError('selected plan changed')
            else:
                self._write_once(dynamic_path, frozen_dynamic)
            payload = dynamic['payload'].encode('utf-8')
            def parse_selected(response):
                validate_envelope(payload, response)
                outcome = SelectedPathCohort._command(self, 'validate-selected-path', selected_case,
                                                       dynamic['bindingSha256'], response.decode('utf-8'))
                if (outcome.get('validated') is not True or outcome.get('bindingSha256') != dynamic['bindingSha256']
                        or outcome.get('maximumNetworkCalls') != 0 or outcome.get('retries') != 0):
                    raise LedgerError('selected path parser failed')
                result = outcome['result']
                if result.get('failure') is not None:
                    raise LedgerError('selected path failure')
                category = result.get('categoryId')
                if category is not None and category != baseline['categoryId']:
                    raise LedgerError('selected path changed category')
                return {'status': 'answered' if category is not None else 'abstained', 'categoryId': category}
            selected = self._exchange([case['id'], 'selected', 'path', 0], payload, responses, parse_selected, expected)
            results[case['id']] = {'baseline': baseline, 'selected': selected}
        attempts = [r['stage_id'] for r in self.coordinator.ledger.stage_history() if r['kind'] == 'attempt']
        if attempts != expected:
            raise CohortIdentityError('unexpected trailing attempts')
        output = require_plain_absolute_path(self.coordinator.root / 'holdout-predictions.json', allow_missing_leaf=True)
        data = _canonical(results)
        if output.exists():
            if output.read_bytes() != data:
                raise CohortIdentityError('published predictions changed')
        else:
            fd, temporary = tempfile.mkstemp(prefix='.holdout-predictions-', suffix='.partial', dir=self.coordinator.root)
            try:
                with os.fdopen(fd, 'wb') as stream:
                    stream.write(data)
                    stream.flush()
                    os.fsync(stream.fileno())
                os.rename(temporary, output)
            finally:
                if os.path.exists(temporary):
                    os.unlink(temporary)
        return results
