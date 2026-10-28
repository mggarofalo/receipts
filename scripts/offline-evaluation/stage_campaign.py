"""Offline stage journal. No HTTP, credentials, retries or model-quality claims.

One immutable case per existing CampaignLedger. Exported plans and dynamic
verification are supplied offline; this is not yet an actual-parser send gate.
"""
from contextlib import contextmanager
import hashlib
from pathlib import Path

from campaign_ledger import CampaignLedger, LedgerBusy, LedgerError, _canonical, require_plain_absolute_path
from guarded_exchange import (_EXCHANGE_OWNER, _exchange_recorded_owned,
                              _parse, verify_artifacts, MODEL)


def digest(data):
    return hashlib.sha256(data).hexdigest()


@contextmanager
def owner():
    if not _EXCHANGE_OWNER.acquire(blocking=False):
        raise LedgerBusy('exchange already owned; no retry')
    try:
        yield
    finally:
        _EXCHANGE_OWNER.release()


def sha(value):
    if not isinstance(value, str) or len(value) != 64 or any(c not in '0123456789abcdef' for c in value):
        raise LedgerError('SHA-256 required')
    return value


class StageCampaign:
    """Persistent baseline -> ALL ordered shortlist batches -> bound verifier.

    A durable start precedes even reservation. An interrupted/failed stage never
    resumes, even when the exchange outcome was already durably successful.
    Multi-case continuation is not implemented. Never reset campaign totals or
    replace the campaign journal to work around this limitation.
    """

    FORMAT = 'offline-stage-v1'

    def _binding_extra(self):
        return {}

    def __init__(self, ledger, plan, *, artifacts, private_root):
        if type(ledger) is not CampaignLedger:
            raise LedgerError('durable campaign ledger required')
        # Canonical round trip detaches caller-owned mutable dictionaries/lists.
        self._plan_bytes = _canonical(plan)
        if (set(self.plan) != {'case', 'plan_sha256', 'production', 'shortlist'}
                or not isinstance(self.plan['case'], str) or not 0 < len(self.plan['case']) <= 100
                or not isinstance(self.plan['shortlist'], list) or not 1 <= len(self.plan['shortlist']) <= 94):
            raise LedgerError('bounded immutable case plan required')
        sha(self.plan['plan_sha256'])
        if any(not isinstance(p, str) or not 0 < len(p.encode('utf-8')) <= 262_144
               or _parse(p).get('model') != MODEL for p in self.payloads):
            raise LedgerError('exact pinned payloads required')
        self.ledger = ledger
        self._artifacts_bytes = _canonical(artifacts)
        self.root = require_plain_absolute_path(private_root)
        if any((p / '.git').exists() for p in (self.root, *self.root.parents)):
            raise LedgerError('private output outside checkout required')
        with owner():
            hashes = verify_artifacts(self.artifacts)
            self._binding_bytes = _canonical({'op': 'bind', 'format': self.FORMAT, 'plan': self.plan,
                            **self._binding_extra(),
                            'artifacts': hashes, 'private_root': str(self.root)})
            history = self.ledger.stage_history()
            if not history:
                self.ledger.append_stage_event(self.binding)
            elif history[0] != {'kind': 'stage', 'event': self.binding}:
                raise LedgerError('campaign case/plan/artifact binding mismatch')
            self._state()

    @property
    def plan(self):
        return _parse(self._plan_bytes)

    @property
    def binding(self):
        return _parse(self._binding_bytes)

    @property
    def artifacts(self):
        return _parse(self._artifacts_bytes)

    @property
    def payloads(self):
        plan = self.plan
        return (plan['production'], *plan['shortlist'])

    @property
    def names(self):
        return ('baseline', *[f'shortlist:{i}' for i in range(len(self.plan['shortlist']))])

    def _state(self):
        if str(self.root) != self.binding['private_root']:
            raise LedgerError('private evidence location changed')
        verify_artifacts(self.artifacts)
        rows = self.ledger.stage_history()
        if not rows or rows[0] != {'kind': 'stage', 'event': self.binding}:
            raise LedgerError('campaign binding changed')
        done, pending, verifier = [], None, None
        attempts, outcomes = {}, {}
        for row in rows[1:]:
            if row['kind'] == 'attempt':
                if pending is None or attempts.keys() - outcomes.keys():
                    raise LedgerError('unbound/interleaved attempt')
                if row['payload_sha256'] != pending['request_sha256']:
                    raise LedgerError('attempt payload mismatch')
                attempts[row['id']] = row
            elif row['kind'] == 'outcome':
                outcomes[row['id']] = row['status']
            else:
                event = row['event']
                if event['op'] == 'start':
                    if pending is not None or event != {'op': 'start', 'index': len(done),
                            'request_sha256': digest(self._payload(len(done), verifier))}:
                        raise LedgerError('invalid stage order')
                    pending = event
                elif event['op'] == 'finish':
                    if pending is None or event['index'] != len(done):
                        raise LedgerError('invalid completion')
                    receipt_path = require_plain_absolute_path(self.root / f'stage-{len(done)}' / 'receipt.json')
                    receipt_bytes = receipt_path.read_bytes()
                    receipt = _parse(receipt_bytes)
                    if (digest(receipt_bytes) != event['receipt_sha256']
                            or receipt['attempt'] not in attempts or outcomes.get(receipt['attempt']) != 'success'
                            or receipt['request_sha256'] != pending['request_sha256']
                            or receipt['artifacts'] != self.binding['artifacts']):
                        raise LedgerError('stage receipt mismatch')
                    for name in ('request', 'response'):
                        path = require_plain_absolute_path(receipt_path.parent / f'{name}.json')
                        if digest(path.read_bytes()) != receipt[f'{name}_sha256']:
                            raise LedgerError('stage evidence drift')
                    done.append(receipt)
                    pending = None
                elif event['op'] in ('verifier', 'verifiers'):
                    if pending is not None or len(done) != len(self.payloads) or verifier is not None:
                        raise LedgerError('invalid verifier binding order')
                    if event['responses'] != [r['response_sha256'] for r in done]:
                        raise LedgerError('verifier response binding mismatch')
                    self._validate_verifier(event, done)
                    verifier = event
                else:
                    raise LedgerError('unknown stage transition')
        if len(attempts) != len(done) + (1 if pending and len(attempts) > len(done) else 0):
            raise LedgerError('interleaved campaign attempt')
        return done, pending, verifier

    def _validate_verifier(self, event, done):
        if event['op'] != 'verifier':
            raise LedgerError('incompatible verifier mode')

    def _expected_stage(self, index, verifier):
        return self.names[index] if index < len(self.names) else 'verifier'

    def _payload(self, index, verifier):
        # Every transition derives the request from the canonical immutable
        # snapshot checked against the durable binding by _state.
        if self.plan != self.binding['plan']:
            raise LedgerError('bound plan changed')
        if index < len(self.payloads):
            return self.payloads[index].encode('utf-8')
        if index == len(self.payloads) and verifier and verifier['payload'] is not None:
            return verifier['payload'].encode('utf-8')
        raise LedgerError('no stage available')

    def _validate_response(self, index, response):
        pass

    def _finish(self, index, receipt):
        verify_artifacts(self.artifacts)
        raw = (self.root / f'stage-{index}' / 'receipt.json').read_bytes()
        self.ledger.append_stage_event({'op': 'finish', 'index': index, 'receipt_sha256': digest(raw)})

    def exchange(self, stage, transport):
        with owner():
            done, pending, verifier = self._state()
            if pending:
                raise LedgerError('unfinished stage; stop and inspect')
            index = len(done)
            expected = self._expected_stage(index, verifier)
            payload = self._payload(index, verifier)
            if stage != expected:
                raise LedgerError('stage skipping/reordering/replay rejected')
            self.ledger.append_stage_event({'op': 'start', 'index': index, 'request_sha256': digest(payload)})
            receipt = _exchange_recorded_owned(self.ledger, payload, transport,
                         artifacts=self.artifacts, private_output=self.root / f'stage-{index}',
                         validate_response=lambda response: self._validate_response(index, response))
            self._finish(index, receipt)
            return receipt

    def bind_verifier(self, payload, plan_sha256):
        """Bind an offline actual-reducer export, or explicit abstention, once.

        This records provenance links; it does not assert the caller actually ran
        the C# reducer. FrozenRunner is the separately executable identity bridge.
        """
        with owner():
            done, pending, verifier = self._state()
            if pending or len(done) != len(self.payloads) or verifier is not None:
                raise LedgerError('all ordered responses required before verifier binding')
            if payload is None:
                if plan_sha256 is not None:
                    raise LedgerError('abstention cannot have verifier hash')
            else:
                sha(plan_sha256)
                if type(payload) is not bytes or not 0 < len(payload) <= 262_144 or _parse(payload).get('model') != MODEL:
                    raise LedgerError('pinned verifier payload required')
            self.ledger.append_stage_event({'op': 'verifier', 'payload': None if payload is None else payload.decode('utf-8'),
                 'plan_sha256': plan_sha256, 'responses': [r['response_sha256'] for r in done]})

    @property
    def complete(self):
        with owner():
            done, pending, verifier = self._state()
            return pending is None and verifier is not None and len(done) == len(self.payloads) + (verifier['payload'] is not None)
