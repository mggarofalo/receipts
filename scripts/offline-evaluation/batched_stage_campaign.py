"""Offline-only durable ordered batched verification through actual frozen C#."""
from campaign_ledger import LedgerError, _canonical, require_plain_absolute_path
from guarded_exchange import _parse, MODEL
from paired_stage_campaign import PairedStageCampaign
from stage_campaign import owner


class BatchedPairedStageCampaign(PairedStageCampaign):
    FORMAT = 'offline-batched-paired-stage-v1'
    PREPARE = 'prepare-batched'
    EMPTY_VERIFICATION = []

    def _replies(self, count):
        raw = [require_plain_absolute_path(self.work / f'stage-{i}' / 'response.json')
               .read_bytes().decode('utf-8') for i in range(count)]
        return {'production': raw[0] if raw else '',
                'shortlist': raw[1:len(self.payloads)],
                'verification': raw[len(self.payloads):]}

    def _export_verifiers(self, done):
        result = self._run(self.PREPARE, self._replies(len(self.payloads)))
        if (result['planSha256'] != self.plan['plan_sha256']
                or result['productionPayload'] != self.plan['production']
                or result['shortlistPayloads'] != self.plan['shortlist']):
            raise LedgerError('actual batched plan export drift')
        batches = result['verificationBatches'] or []
        for batch in batches:
            if _parse(batch['payload']).get('model') != MODEL:
                raise LedgerError('pinned batch payload required')
        return {'op': 'verifiers', 'batches': batches,
                'plan_sha256': result['verificationSha256'],
                'responses': [r['response_sha256'] for r in done[:len(self.payloads)]]}

    def _validate_verifier(self, event, done):
        # Re-export from the complete immutable shortlist on reopen as well.
        # No caller-owned batch, partial vector or cross-plan binding is trusted.
        if event != self._export_verifiers(done):
            raise LedgerError('actual complete verification vector mismatch')

    def bind_verifier(self):
        with owner():
            done, pending, verifier = self._state()
            if pending or len(done) != len(self.payloads) or verifier is not None:
                raise LedgerError('all actual-parser validated shortlist responses required')
            # One fsynced record contains every batch, including global coverage.
            self.ledger.append_stage_event(self._export_verifiers(done))

    def _payload(self, index, verifier):
        if index < len(self.payloads):
            return super()._payload(index, verifier)
        offset = index - len(self.payloads)
        if verifier and verifier['op'] == 'verifiers' and offset < len(verifier['batches']):
            return verifier['batches'][offset]['payload'].encode('utf-8')
        raise LedgerError('no batched stage available')

    def _expected_stage(self, index, verifier):
        if index < len(self.names):
            return self.names[index]
        offset = index - len(self.names)
        if verifier and offset < len(verifier['batches']):
            batch = verifier['batches'][offset]
            return 'global-coverage' if batch['isGlobalCoverage'] else f'verifier:{offset}'
        raise LedgerError('no batched stage available')

    def _validate_response(self, index, response):
        replies = self._replies(index + 1)
        result = self._run('validate-batched', replies)
        if result != {'validated': True, 'maximumNetworkCalls': 0}:
            raise LedgerError('actual batched parser did not validate response')
        if index >= len(self.payloads):
            done, _, verifier = self._state()
            if index + 1 == len(self.payloads) + len(verifier['batches']):
                # Full aggregation, not just prefix validation, precedes success.
                result = self._run('replay-batched', replies)
                if (result['verificationSha256'] != verifier['plan_sha256']
                        or result['candidate']['failure'] is not None):
                    raise LedgerError('actual complete batched aggregation failed')

    @property
    def complete(self):
        with owner():
            done, pending, verifier = self._state()
            return (pending is None and verifier is not None
                    and len(done) == len(self.payloads) + len(verifier['batches']))

    @property
    def result(self):
        with owner():
            done, pending, verifier = self._state()
            if pending or verifier is None or len(done) != len(self.payloads) + len(verifier['batches']):
                raise LedgerError('complete ordered verification required before result')
            return self._run('replay-batched', self._replies(len(done)))
