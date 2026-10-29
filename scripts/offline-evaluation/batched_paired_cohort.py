"""Paired cohort scheduler using the actual C# offline exchange."""
import hashlib
import json
import os
import shutil
from pathlib import Path
import subprocess
import sys
import tempfile
import threading

from campaign_ledger import LedgerError, RESERVATION_NANODOLLARS, _canonical, _unique, _valid_stage_id, require_plain_absolute_path
from guarded_exchange import RecordedTransport, validate_envelope
from offline_coordinator import OfflineCoordinator
from cohort_live_transport import CohortLiveTransport


_COHORT_OWNER = threading.Lock()


class CohortIdentityError(LedgerError):
    """Frozen cohort, executable or plan changed; stop the whole run."""


class BatchedPairedCohort:
    """A fixed ordered set of case IDs and inputs sharing one coordinator.

    Inputs are bound by an exclusively created manifest before any reservation.
    Callers must preserve the private directory and ledger together. This is a
    recorded scheduler with an explicit opt-in live transport.
    """
    def __init__(self, coordinator, host, dll, cases, *, aggregate_call_limit, prior_calls, live_transport=None):
        if type(coordinator) is not OfflineCoordinator or not isinstance(cases, list) or not cases:
            raise LedgerError('coordinator and nonempty ordered cohort required')
        if (type(aggregate_call_limit) is not int or not 1 <= aggregate_call_limit <= 96
                or type(prior_calls) is not int or not 0 <= prior_calls < aggregate_call_limit):
            raise LedgerError('explicit aggregate limit and prior call count required')
        if live_transport is not None and type(live_transport) is not CohortLiveTransport:
            raise LedgerError('invalid live transport')
        self.live_transport = live_transport
        self._call_allowance = {'aggregate_call_limit': aggregate_call_limit, 'prior_calls': prior_calls}
        snapshot = coordinator.ledger.snapshot()
        self._policy = snapshot['policy']
        if (self._policy['max_calls'] > aggregate_call_limit - prior_calls
                or snapshot['attempts'] + prior_calls > aggregate_call_limit
                or self._policy['unknown_carry_nanodollars'] < prior_calls * RESERVATION_NANODOLLARS):
            raise LedgerError('ledger exceeds aggregate call allowance or prior carry')
        ids = []
        for case in cases:
            if (not isinstance(case, dict) or set(case) != {'id', 'input'}
                    or not _valid_stage_id([case['id'], 'baseline', 'production', 0])
                    or not isinstance(case['input'], dict)):
                raise LedgerError('invalid cohort case')
            ids.append(case['id'])
        if len(set(ids)) != len(ids):
            raise LedgerError('duplicate case ID')
        try:
            self.cases = json.loads(_canonical(cases), object_pairs_hook=_unique)
        except (ValueError, TypeError) as error:
            raise LedgerError('invalid cohort JSON') from error
        self.coordinator = coordinator
        self.host = str(require_plain_absolute_path(Path(shutil.which(str(host)) or str(host))))
        self.dll = str(require_plain_absolute_path(Path(dll)))
        self._identity = self._build_identity()
        path = require_plain_absolute_path(coordinator.root / 'paired-cohort.json', allow_missing_leaf=True)
        if path.exists():
            try:
                old = json.loads(path.read_bytes(), object_pairs_hook=_unique)
                if (old.get('format') != 'batched-paired-cohort-v1' or old.get('cases') != self.cases
                        or old.get('build') != self._identity
                        or old.get('transport') != self._transport_identity()
                        or old.get('policy') != self._policy
                        or old.get('call_allowance') != self._call_allowance):
                    raise CohortIdentityError('cohort or build changed')
            except (OSError, ValueError, AttributeError) as error:
                raise CohortIdentityError('invalid or changed cohort manifest') from error
        elif coordinator.ledger.stage_history():
            raise CohortIdentityError('cannot bind cohort after reservations')
        empty = {'production': '', 'shortlist': [], 'verification': []}
        plans = []
        for item in self.cases:
            prepared = self._command('prepare-batched', item, empty)
            plan = prepared.get('planSha256')
            if prepared.get('maximumNetworkCalls') != 0 or not isinstance(plan, str) or len(plan) != 64:
                raise CohortIdentityError('invalid offline plan')
            plans.append(plan)
        self._plans = plans
        manifest = _canonical({'format': 'batched-paired-cohort-v1', 'cases': self.cases,
                               'build': self._identity, 'plans': plans, 'transport': self._transport_identity(),
                               'policy': self._policy, 'call_allowance': self._call_allowance})
        self._manifest, self._manifest_path = manifest, path
        if path.exists():
            if path.read_bytes() != manifest:
                raise CohortIdentityError('cohort, build or plan changed')
        else:
            if coordinator.ledger.stage_history():
                raise LedgerError('cannot bind cohort after reservations')
            with path.open('xb') as stream:
                stream.write(manifest)
                stream.flush()
                os.fsync(stream.fileno())

    def _transport_identity(self):
        return self.live_transport.identity if self.live_transport else {'mode': 'recorded'}

    def _build_identity(self):
        # Bind the host, the runner output's managed files and the Python
        # coordinator sources. This is a change detector, not attestation of
        # the build, framework runtime, or a race-proof filesystem snapshot.
        try:
            directory = Path(self.dll).parent
            files = [Path(self.host)] + sorted(p for p in directory.iterdir()
                     if p.is_file() and p.suffix.lower() in ('.dll', '.json'))
            files += [Path(__file__).resolve().parent / name for name in
                      ('batched_paired_cohort.py', 'offline_coordinator.py', 'campaign_ledger.py',
                       'guarded_exchange.py', 'stage_campaign.py', 'cohort_live_transport.py',
                       'live_https_worker.py')]
            if self.live_transport is not None:
                files += [Path(sys.executable).resolve(), Path(__file__).resolve().parent / 'live_stage_campaign.py',
                          Path(__file__).resolve().parent / 'safe_os_diagnostics.py',
                          Path(__file__).resolve().parent / 'batched_cohort_operator.py']
            if Path(self.dll) not in files:
                raise CohortIdentityError('runner DLL missing')
            return {str(require_plain_absolute_path(path)): hashlib.sha256(path.read_bytes()).hexdigest()
                    for path in files}
        except (OSError, LedgerError) as error:
            raise CohortIdentityError('build identity unavailable') from error

    def _check_identity(self):
        if self._build_identity() != self._identity:
            raise CohortIdentityError('build identity changed')

    def _command(self, command, case, replies):
        self._check_identity()
        # Unique private scratch directory; C# requires output to not exist.
        with tempfile.TemporaryDirectory(dir=self.coordinator.root) as directory:
            root = Path(directory)
            input_path, reply_path, output_path = (root / name for name in ('input.json', 'replies.json', 'output.json'))
            input_path.write_bytes(_canonical(case['input']))
            reply_path.write_bytes(_canonical(replies))
            try:
                proc = subprocess.run([self.host, self.dll, command, str(input_path), str(reply_path), str(output_path)],
                    stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0), timeout=60)
                if proc.returncode:
                    raise LedgerError('actual C# exchange rejected input or reply')
                return json.loads(output_path.read_bytes(), object_pairs_hook=_unique)
            except (OSError, subprocess.SubprocessError, ValueError) as error:
                raise LedgerError('actual C# exchange failed') from error

    def _bind_vector(self, case, initial, dynamic):
        batches = dynamic.get('verificationBatches') or []
        if (dynamic.get('maximumNetworkCalls') != 0
                or dynamic.get('planSha256') != initial.get('planSha256')
                or dynamic.get('productionPayload') != initial.get('productionPayload')
                or dynamic.get('shortlistPayloads') != initial.get('shortlistPayloads')
                or not isinstance(batches, list)
                or 1 + len(initial['shortlistPayloads']) + len(batches) > 30):
            raise CohortIdentityError('batched plan changed or exceeds bound')
        verification_hash = dynamic.get('verificationSha256')
        if (bool(batches) != (verification_hash is not None)
                or (batches and (not isinstance(verification_hash, str) or len(verification_hash) != 64))):
            raise CohortIdentityError('invalid batched verification binding')
        stages = []
        saw_coverage = False
        for index, batch in enumerate(batches):
            if (not isinstance(batch, dict) or batch.get('index') != index
                    or type(batch.get('isGlobalCoverage')) is not bool
                    or not isinstance(batch.get('payload'), str) or not batch['payload']):
                raise CohortIdentityError('invalid verification vector')
            coverage = batch['isGlobalCoverage']
            if saw_coverage and not coverage:
                raise CohortIdentityError('coverage must follow paths')
            saw_coverage |= coverage
            stages.append([case, 'candidate', 'coverage' if coverage else 'verification', index])
        if batches and (batches[0]['isGlobalCoverage'] or not saw_coverage):
            raise CohortIdentityError('complete paths and coverage required')
        binding = _canonical({'plan': initial['planSha256'], 'verification': verification_hash,
                              'batches': batches})
        path = require_plain_absolute_path(self.coordinator.root / (case + '.verification-vector'), allow_missing_leaf=True)
        history = self.coordinator.ledger.stage_history()
        dynamic_attempts = [r['stage_id'] for r in history if r['kind'] == 'attempt'
                            and r.get('stage_id', [None])[0] == case
                            and r['stage_id'][2] in ('verification', 'coverage')]
        if dynamic_attempts != stages[:len(dynamic_attempts)]:
            raise CohortIdentityError('dynamic stage order changed')
        if path.exists():
            if path.read_bytes() != binding:
                raise CohortIdentityError('verification vector changed')
        else:
            if dynamic_attempts:
                raise CohortIdentityError('verification vector unavailable')
            partial = require_plain_absolute_path(self.coordinator.root / (case + '.verification-partial'), allow_missing_leaf=True)
            with partial.open('xb') as stream:
                stream.write(binding)
                stream.flush()
                os.fsync(stream.fileno())
            os.rename(partial, path)
        return batches, stages, verification_hash

    def _require_vector_allowance(self, stages, batches):
        history = self.coordinator.ledger.stage_history()
        reserved = {tuple(row['stage_id']) for row in history
                    if row['kind'] == 'attempt' and 'stage_id' in row}
        remaining = sum(tuple(stage) not in reserved for stage in stages)
        snapshot = self.coordinator.ledger.snapshot()
        policy = snapshot['policy']
        if any(not 0 < len(batch['payload'].encode('utf-8')) <= policy['max_payload_bytes'] for batch in batches):
            raise LedgerError('verification vector exceeds payload allowance')
        if (snapshot['attempts'] + remaining > policy['max_calls']
                or snapshot['reserved_nanodollars'] + remaining * RESERVATION_NANODOLLARS > policy['budget_nanodollars']):
            raise LedgerError('remaining allowance cannot cover complete verification vector')

    def run_case(self, case, responses):
        """Run recorded stages; completed stages are reparsed without resending."""
        if not _COHORT_OWNER.acquire(blocking=False):
            raise LedgerError('another batched cohort is running')
        try:
            return self._run_case(case, responses)
        finally:
            _COHORT_OWNER.release()

    def _run_case(self, case, responses):
        try:
            path = require_plain_absolute_path(self._manifest_path)
            current_manifest = path.read_bytes()
        except (OSError, LedgerError) as error:
            raise CohortIdentityError('cohort manifest unavailable') from error
        if current_manifest != self._manifest or _canonical({'format': 'batched-paired-cohort-v1', 'cases': self.cases, 'build': self._identity, 'plans': self._plans, 'transport': self._transport_identity(), 'policy': self._policy, 'call_allowance': self._call_allowance}) != self._manifest:
            raise CohortIdentityError('cohort, build or plan changed')
        snapshot = self.coordinator.ledger.snapshot()
        if (snapshot['policy'] != self._policy or
                snapshot['attempts'] + self._call_allowance['prior_calls'] > self._call_allowance['aggregate_call_limit']):
            raise CohortIdentityError('aggregate call allowance changed or exceeded')
        matches = [item for item in self.cases if item['id'] == case]
        if len(matches) != 1 or not isinstance(responses, dict):
            raise LedgerError('unknown case or recorded responses')
        item = matches[0]
        replies = {'production': '', 'shortlist': [], 'verification': []}
        initial = self._command('prepare-batched', item, replies)
        if initial.get('maximumNetworkCalls') != 0 or initial.get('planSha256') != self._plans[self.cases.index(item)]:
            raise CohortIdentityError('offline batched plan changed')
        verification_hash = None
        batches = []

        def exchange(arm, phase, index, payload):
            stage = [case, arm, phase, index]
            recorded = responses.get((arm, phase, index))
            if self.live_transport is not None and recorded is not None:
                raise LedgerError('recorded responses forbidden in live mode')
            if recorded is None and self.live_transport is None:
                if not any(r['kind'] == 'attempt' and r.get('stage_id') == stage
                           for r in self.coordinator.ledger.stage_history()):
                    raise LedgerError('missing recorded response')
                recorded = b''
            if self.live_transport is None and type(recorded) is not bytes:
                raise LedgerError('recorded response bytes required')
            payload = payload.encode('utf-8')
            def parse(raw):
                try:
                    validate_envelope(payload, raw)
                except (TypeError, ValueError, KeyError) as error:
                    raise LedgerError('invalid recorded response') from error
                trial = {'production': replies['production'], 'shortlist': list(replies['shortlist']),
                         'verification': list(replies['verification'])}
                if phase == 'production':
                    trial['production'] = raw.decode('utf-8')
                elif phase == 'shortlist':
                    trial['shortlist'].append(raw.decode('utf-8'))
                else:
                    trial['verification'].append(raw.decode('utf-8'))
                result = self._command('validate-batched', item, trial)
                if result != {'validated': True, 'maximumNetworkCalls': 0}:
                    raise LedgerError('actual C# prefix validation failed')
                if phase in ('verification', 'coverage') and len(trial['verification']) == len(batches):
                    result = self._command('replay-batched', item, trial)
                    if result['verificationSha256'] != verification_hash or result['candidate']['failure'] is not None:
                        raise LedgerError('actual complete batched aggregation failed')
                return raw.decode('utf-8')
            return self.coordinator.execute(stage, payload, self.live_transport or RecordedTransport(recorded), parse)

        replies['production'] = exchange('baseline', 'production', 0, initial['productionPayload'])
        for index, payload in enumerate(initial['shortlistPayloads']):
            replies['shortlist'].append(exchange('candidate', 'shortlist', index, payload))
        dynamic = self._command('prepare-batched', item, replies)
        batches, stages, verification_hash = self._bind_vector(case, initial, dynamic)
        self._require_vector_allowance(stages, batches)
        for stage, batch in zip(stages, batches):
            replies['verification'].append(exchange(stage[1], stage[2], stage[3], batch['payload']))
        result = self._command('replay-batched', item, replies)
        if result['verificationSha256'] != verification_hash:
            raise CohortIdentityError('verification aggregation changed')
        baseline = result['production']
        suggestion = baseline['suggestion']
        candidate = result['candidate']
        def prediction(category, failure):
            return {'status': 'failed' if failure is not None else
                    ('answered' if category is not None else 'abstained'), 'categoryId': category}
        return {'baseline': prediction(suggestion['categoryId'] if suggestion else None, baseline['failure']),
                'candidate': prediction(candidate['categoryId'], candidate['failure'])}

    def run(self, responses):
        """Return per-case outcomes; continue after case failures, never after identity changes."""
        if not isinstance(responses, dict) or set(responses) - {c['id'] for c in self.cases}:
            raise LedgerError('responses must name cohort cases')
        results = {}
        for case in self.cases:
            try:
                results[case['id']] = {'status': 'complete', 'predictions':
                    self.run_case(case['id'], responses.get(case['id'], {}))}
            except CohortIdentityError:
                raise
            except (LedgerError, OSError, UnicodeError):
                results[case['id']] = {'status': 'unresolved'}
        return results
