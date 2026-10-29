"""Named-stage coordinator for recorded and explicit live transport.

The caller supplies request bytes, a parser, and an explicit transport. A named, charged reservation precedes consuming the response. A
complete response file is flushed before publication and before parsing.
"""
import base64
import hashlib
import json
import os
from pathlib import Path

from campaign_ledger import CampaignLedger, LedgerError, _canonical, _unique, _valid_stage_id, require_plain_absolute_path
from guarded_exchange import MAX_RESPONSE_BYTES, RecordedTransport
from cohort_live_transport import CohortLiveTransport
from stage_campaign import owner


class UnresolvedStage(LedgerError):
    """A reserved stage has no complete response; it must not be resent."""


def _hash(data):
    return hashlib.sha256(data).hexdigest()


class OfflineCoordinator:
    """One ledger and independent named stages.

    A completed response can be parsed again after interruption. Missing or
    incomplete response evidence is unknown, not permission to dispatch again.
    All files and the ledger require a stable private local directory; external
    modification or rollback is outside this local accounting mechanism.
    """

    def __init__(self, ledger, private_root):
        if type(ledger) is not CampaignLedger:
            raise LedgerError('campaign ledger required')
        self.ledger = ledger
        self.root = require_plain_absolute_path(private_root)
        if not self.root.is_dir() or any((p / '.git').exists() for p in (self.root, *self.root.parents)):
            raise LedgerError('private output directory outside checkout required')

    def execute(self, stage_id, payload, transport, parse, *, pre_reserve=None):
        with owner():
            return self._execute(stage_id, payload, transport, parse, pre_reserve)

    def _execute(self, stage_id, payload, transport, parse, pre_reserve=None):
        if not _valid_stage_id(stage_id) or type(payload) is not bytes or not callable(parse):
            raise LedgerError('valid stage, request bytes and parser required')
        if pre_reserve is not None and not callable(pre_reserve):
            raise LedgerError('invalid pre-reservation guard')
        history = self.ledger.stage_history()
        if any(row['kind'] == 'stage' or (row['kind'] == 'attempt' and 'stage_id' not in row) for row in history):
            raise LedgerError('legacy stage events or unnamed reservations cannot be coordinated')
        matches = [r for r in history if r['kind'] == 'attempt' and r.get('stage_id') == stage_id]
        if matches:
            attempt = matches[0]
            if attempt['payload_sha256'] != _hash(payload) or attempt['payload_bytes'] != len(payload):
                raise LedgerError('stage request changed')
        else:
            outcomes = {r['id']: r['status'] for r in history if r['kind'] == 'outcome'}
            if any(r['kind'] == 'attempt' and r['stage_id'][0] == stage_id[0]
                   and outcomes.get(r['id']) != 'success' for r in history if r['kind'] == 'attempt'):
                raise UnresolvedStage('an earlier stage in this case is unresolved')
            if type(transport) not in (RecordedTransport, CohortLiveTransport):
                raise LedgerError('supported transport required for a new stage')
            if pre_reserve is not None:
                pre_reserve()
            attempt_id = self.ledger.reserve_attempt(payload, stage_id=stage_id)
            attempt = {'id': attempt_id, 'stage_id': stage_id, 'payload_sha256': _hash(payload), 'payload_bytes': len(payload)}
            response = transport.exchange(payload)
            if type(response) is not bytes or not 0 < len(response) <= MAX_RESPONSE_BYTES:
                raise LedgerError('invalid response size; stage unresolved')
            self._persist(attempt, response)

        response = self._load(attempt)
        result = parse(response)
        outcomes = [r['status'] for r in self.ledger.stage_history()
                    if r['kind'] == 'outcome' and r['id'] == attempt['id']]
        if outcomes and outcomes != ['success']:
            raise LedgerError('stage outcome is not successful')
        if not outcomes:
            self.ledger.record_outcome(attempt['id'], 'success')
        return result

    def _path(self, attempt, suffix):
        return require_plain_absolute_path(self.root / (attempt['id'] + suffix), allow_missing_leaf=True)

    def _persist(self, attempt, response):
        record = {'attempt': attempt['id'], 'stage_id': attempt['stage_id'],
                  'request_sha256': attempt['payload_sha256'], 'response_sha256': _hash(response),
                  'response': base64.b64encode(response).decode('ascii')}
        temporary = self._path(attempt, '.partial')
        final = self._path(attempt, '.response')
        with temporary.open('xb') as stream:
            stream.write(_canonical(record))
            stream.flush()
            os.fsync(stream.fileno())
        # Windows rename does not overwrite an existing destination. An interrupted
        # partial file alone is unknown, even if it happens to contain all bytes.
        os.rename(temporary, final)

    def _load(self, attempt):
        path = self._path(attempt, '.response')
        if not path.exists():
            raise UnresolvedStage('reserved stage has no complete response; no retry')
        with path.open('rb') as stream:
            raw = stream.read(2 * MAX_RESPONSE_BYTES + 1)
        if len(raw) > 2 * MAX_RESPONSE_BYTES:
            raise LedgerError('response evidence too large')
        try:
            record = json.loads(raw, object_pairs_hook=_unique)
            if set(record) != {'attempt', 'stage_id', 'request_sha256', 'response_sha256', 'response'}:
                raise ValueError('response record fields')
            response = base64.b64decode(record['response'], validate=True)
            if (record['attempt'] != attempt['id'] or record['stage_id'] != attempt['stage_id']
                    or record['request_sha256'] != attempt['payload_sha256']
                    or record['response_sha256'] != _hash(response)
                    or not 0 < len(response) <= MAX_RESPONSE_BYTES):
                raise ValueError('response record binding')
        except (ValueError, TypeError, KeyError, UnicodeError) as error:
            raise LedgerError('invalid response evidence') from error
        return response
