"""Paired cohort scheduler using the actual C# offline exchange."""
import hashlib
import json
import os
import shutil
from pathlib import Path
import subprocess
import sys
import tempfile

from campaign_ledger import LedgerError, _canonical, _unique, _valid_stage_id, require_plain_absolute_path
from guarded_exchange import RecordedTransport, validate_envelope
from offline_coordinator import OfflineCoordinator
from cohort_live_transport import CohortLiveTransport


class CohortIdentityError(LedgerError):
    """Frozen cohort, executable or plan changed; stop the whole run."""


class PairedCohort:
    """A fixed ordered set of case IDs and inputs sharing one coordinator.

    Inputs are bound by an exclusively created manifest before any reservation.
    Callers must preserve the private directory and ledger together. This is a
    local scheduler; the optional live transport is an explicit opt-in.
    """
    def __init__(self, coordinator, host, dll, cases, *, live_transport=None):
        if type(coordinator) is not OfflineCoordinator or not isinstance(cases, list) or not cases:
            raise LedgerError('coordinator and nonempty ordered cohort required')
        if live_transport is not None and type(live_transport) is not CohortLiveTransport:
            raise LedgerError('invalid live transport')
        self.live_transport = live_transport
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
                if (old.get('format') != 'paired-cohort-v2' or old.get('cases') != self.cases
                        or old.get('build') != self._identity
                        or old.get('transport') != self._transport_identity()):
                    raise CohortIdentityError('cohort or build changed')
            except (OSError, ValueError, AttributeError) as error:
                raise CohortIdentityError('invalid or changed cohort manifest') from error
        elif coordinator.ledger.stage_history():
            raise CohortIdentityError('cannot bind cohort after reservations')
        empty = {'production': '', 'shortlist': [], 'verification': None}
        plans = []
        for item in self.cases:
            prepared = self._command('prepare', item, empty)
            plan = prepared.get('planSha256')
            if prepared.get('maximumNetworkCalls') != 0 or not isinstance(plan, str) or len(plan) != 64:
                raise CohortIdentityError('invalid offline plan')
            plans.append(plan)
        self._plans = plans
        manifest = _canonical({'format': 'paired-cohort-v2', 'cases': self.cases,
                               'build': self._identity, 'plans': plans, 'transport': self._transport_identity()})
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
                      ('paired_cohort.py', 'offline_coordinator.py', 'campaign_ledger.py',
                       'guarded_exchange.py', 'stage_campaign.py', 'cohort_live_transport.py',
                       'live_https_worker.py')]
            if self.live_transport is not None:
                files += [Path(sys.executable).resolve(), Path(__file__).resolve().parent / 'live_stage_campaign.py']
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

    def run_case(self, case, responses):
        """Run one frozen case. responses maps (arm, phase, index) to bytes.

        Existing completed stages never consume the supplied recorded transport.
        Missing recorded bytes are rejected before any new reservation.
        """
        try:
            path = require_plain_absolute_path(self._manifest_path)
            current_manifest = path.read_bytes()
        except (OSError, LedgerError) as error:
            raise CohortIdentityError('cohort manifest unavailable') from error
        if current_manifest != self._manifest or _canonical({'format': 'paired-cohort-v2', 'cases': self.cases, 'build': self._identity, 'plans': self._plans, 'transport': self._transport_identity()}) != self._manifest:
            raise CohortIdentityError('cohort, build or plan changed')
        matches = [item for item in self.cases if item['id'] == case]
        if len(matches) != 1 or not isinstance(responses, dict):
            raise LedgerError('unknown case or recorded responses')
        item = matches[0]
        replies = {'production': '', 'shortlist': [], 'verification': None}
        initial = self._command('prepare', item, replies)
        if initial.get('maximumNetworkCalls') != 0 or initial.get('planSha256') != self._plans[self.cases.index(item)]:
            raise CohortIdentityError('offline plan changed')

        def exchange(arm, phase, index, payload):
            stage = [case, arm, phase, index]
            key = (arm, phase, index)
            recorded = responses.get(key)
            if self.live_transport is not None and recorded is not None:
                raise LedgerError('recorded responses forbidden in live mode')
            if recorded is None and self.live_transport is None:
                # The coordinator ignores this transport for a retained response;
                # for a new stage it must fail before reservation.
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
                         'verification': replies['verification']}
                if phase == 'production':
                    trial['production'] = raw.decode('utf-8')
                elif phase == 'shortlist':
                    trial['shortlist'].append(raw.decode('utf-8'))
                else:
                    trial['verification'] = raw.decode('utf-8')
                result = self._command('validate', item, trial)
                if result != {'validated': True, 'maximumNetworkCalls': 0}:
                    raise LedgerError('actual C# validation failed')
                return raw.decode('utf-8')
            return self.coordinator.execute(stage, payload, self.live_transport or RecordedTransport(recorded), parse)

        replies['production'] = exchange('baseline', 'production', 0, initial['productionPayload'])
        for index, payload in enumerate(initial['shortlistPayloads']):
            replies['shortlist'].append(exchange('candidate', 'shortlist', index, payload))
        dynamic = self._command('prepare', item, replies)
        if dynamic.get('planSha256') != initial.get('planSha256') or dynamic.get('maximumNetworkCalls') != 0:
            raise CohortIdentityError('paired plan changed')
        if dynamic['verificationPayload'] is not None:
            replies['verification'] = exchange('candidate', 'verification', 0, dynamic['verificationPayload'])
        result = self._command('replay', item, replies)
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
