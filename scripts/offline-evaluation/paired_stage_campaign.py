"""Single-case offline controller authenticated by the frozen C# executable.

Only recorded transport is exposed. Each response must pass the real parser
before its reservation receives success or the stage can complete.
"""
import os
from uuid import uuid4

from campaign_ledger import LedgerError, _canonical, require_plain_absolute_path
from frozen_runner import FrozenRunner
from guarded_exchange import _parse
from stage_campaign import StageCampaign, digest, owner


class PairedStageCampaign(StageCampaign):
    FORMAT = 'offline-paired-stage-v1'
    PREPARE = 'prepare'
    EMPTY_VERIFICATION = None

    def __init__(self, ledger, runner, input_path, *, case, private_root):
        if type(runner) is not FrozenRunner:
            raise LedgerError('actual frozen runner required')
        self.runner = runner
        self.input = require_plain_absolute_path(input_path)
        if not 0 < self.input.stat().st_size <= 8 * 1024 * 1024:
            raise LedgerError("bounded input required")
        self.input_hash = digest(self.input.read_bytes())
        self.work = require_plain_absolute_path(private_root)
        if not self.work.is_dir() or any((p / '.git').exists() for p in (self.work, *self.work.parents)):
            raise LedgerError('private output outside checkout required')
        self._identity = _canonical({'input': str(self.input), 'input_sha256': self.input_hash,
                                    'roots': runner.roots, 'host': runner.host, 'assembly': runner.assembly})
        exported = self._run(self.PREPARE, {'production': '', 'shortlist': [], 'verification': self.EMPTY_VERIFICATION})
        plan = {'case': case, 'plan_sha256': exported['planSha256'],
                'production': exported['productionPayload'], 'shortlist': exported['shortlistPayloads']}
        super().__init__(ledger, plan, artifacts=runner.artifacts, private_root=private_root)

    def _binding_extra(self):
        return {'executor': _parse(self._identity)}

    def _verify_runner(self):
        require_plain_absolute_path(self.input)
        require_plain_absolute_path(self.work)
        self.runner.verify()
        if hasattr(self, '_artifacts_bytes') and self.runner.artifacts != self.artifacts:
            raise LedgerError('frozen input or runner drift')
        identity = {'input': str(self.input), 'input_sha256': self.input_hash,
                    'roots': self.runner.roots, 'host': self.runner.host, 'assembly': self.runner.assembly}
        if _canonical(identity) != self._identity or digest(self.input.read_bytes()) != self.input_hash:
            raise LedgerError('executed identity drift')

    def _run(self, command, replies):
        self._verify_runner()
        # Unique CreateNew evidence survives failures and restart; no overwrite.
        name = str(uuid4())
        reply_path = require_plain_absolute_path(self.work / (name + '-replies.json'), allow_missing_leaf=True)
        with reply_path.open('xb') as stream:
            stream.write(_canonical(replies))
            stream.flush()
            os.fsync(stream.fileno())
        result = self.runner.run(command, self.input, reply_path, self.work / (name + '-output.json'))
        self._verify_runner()
        return result

    def _state(self):
        self._verify_runner()
        return super()._state()

    def _replies(self, count):
        raw = [require_plain_absolute_path(self.work / f'stage-{i}' / 'response.json').read_bytes().decode('utf-8')
               for i in range(count)]
        return {'production': raw[0] if raw else '',
                'shortlist': raw[1:len(self.payloads)],
                'verification': raw[len(self.payloads)] if len(raw) > len(self.payloads) else None}

    def _validate_response(self, index, response):
        result = self._run('validate', self._replies(index + 1))
        if result != {'validated': True, 'maximumNetworkCalls': 0}:
            raise LedgerError('actual parser did not validate response')

    def bind_verifier(self):
        with owner():
            done, pending, verifier = self._state()
            if pending or len(done) != len(self.payloads) or verifier is not None:
                raise LedgerError('all actual-parser validated shortlist responses required')
            result = self._run('prepare', self._replies(len(done)))
            if (result['planSha256'] != self.plan['plan_sha256']
                    or result['productionPayload'] != self.plan['production']
                    or result['shortlistPayloads'] != self.plan['shortlist']):
                raise LedgerError('actual plan export drift')
            self.ledger.append_stage_event({'op': 'verifier', 'payload': result['verificationPayload'],
                'plan_sha256': result['verificationSha256'],
                'responses': [r['response_sha256'] for r in done]})
