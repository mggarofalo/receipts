"""Invented complete taxonomy through frozen C#; no provider access."""
import json
import os
import unittest
from unittest.mock import patch

from batched_stage_campaign import PartitionedBatchedPairedStageCampaign
from campaign_ledger import CampaignLedger, LedgerError, Policy, _record_kind
from guarded_exchange import RecordedTransport
import test_batched_stage_campaign as legacy


class PartitionedSchemaTests(unittest.TestCase):
    def event(self):
        return {'op': 'verifiers-v2', 'batches': [
            {'index': i, 'isGlobalCoverage': i >= 4, 'payload': '{}',
             'questionIds': [f'q-{i}']} for i in range(7)],
            'plan_sha256': 'a' * 64, 'responses': ['b' * 64] * 3}

    def check(self, event):
        return _record_kind({'kind': 'stage', 'event': event})

    def test_complete_contiguous_partitions_and_legacy_unchanged(self):
        self.assertEqual(self.check(self.event()), 'verifiers-v2')
        event = self.event()
        event['op'] = 'verifiers'
        with self.assertRaises(LedgerError):
            self.check(event)
        for damage in ('coverage-first', 'path-after-coverage', 'no-coverage', 'calls', 'index'):
            with self.subTest(damage=damage):
                event = self.event()
                if damage == 'coverage-first':
                    event['batches'][0]['isGlobalCoverage'] = True
                elif damage == 'path-after-coverage':
                    event['batches'][5]['isGlobalCoverage'] = False
                elif damage == 'no-coverage':
                    for batch in event['batches']:
                        batch['isGlobalCoverage'] = False
                elif damage == 'calls':
                    event['responses'] *= 10
                else:
                    event['batches'][2]['index'] = 3
                with self.assertRaises(LedgerError):
                    self.check(event)


@unittest.skipUnless(os.name == 'nt', 'Windows ledger and frozen runtime')
class ActualPartitionedStageTests(unittest.TestCase):
    setUpClass = classmethod(legacy.ActualBatchedStageTests.setUpClass.__func__)
    setUp = legacy.ActualBatchedStageTests.setUp
    response = legacy.ActualBatchedStageTests.response
    # Reuse frozen runtime and synthetic reply generator, not v1 stage names.
    def controller(self, ledger):
        return PartitionedBatchedPairedStageCampaign(ledger, self.runner, self.input,
            case='invented-apples', private_root=self.private)

    def large(self, ledger):
        data = json.loads(self.input.read_text())
        data['taxonomy'] = [{'id': f'item-{i}', 'name': f'Fruit {i}',
            'definition': f'Invented fruit {i}: ' + f'{i:02d}' * 1700,
            'parentId': None} for i in range(12)]
        self.assertGreater(len(json.dumps(data['taxonomy']).encode()), 40000)
        self.input.write_text(json.dumps(data))
        campaign = self.controller(ledger)
        for i, payload in enumerate(campaign.payloads):
            response = self.response(payload)
            if i:
                body = json.loads(response)
                # One plausible path keeps the fixture small. All twelve
                # alternatives still appear in complete coverage partitions.
                for key, answer in body['answers'].items():
                    answer['noul'] = .95 if key.endswith('_plausible_0') else .05
                response = json.dumps(body).encode()
            campaign.exchange(campaign.names[i], RecordedTransport(response))
        campaign.bind_verifier()
        batches = ledger.stage_history()[-1]['event']['batches']
        self.assertGreaterEqual(sum(b['isGlobalCoverage'] for b in batches), 2)
        self.assertTrue(any(not b['isGlobalCoverage'] for b in batches))
        return campaign, ledger.stage_history()[-1]['event']['batches']

    def test_initial_known_call_overflow_before_any_reservation(self):
        data = json.loads(self.input.read_text())
        data['taxonomy'] = [{'id': f'c{i:04}', 'name': 'C', 'definition': 'G',
                             'parentId': None if i < 4 else f'c{i % 4:04}'} for i in range(433)]
        self.input.write_text(json.dumps(data))
        with CampaignLedger(self.ledger_path) as ledger:
            with self.assertRaises(LedgerError):
                self.controller(ledger)
            self.assertEqual(ledger.snapshot()['attempts'], 0)
            self.assertEqual(ledger.stage_history(), [])

    def test_dynamic_complete_call_overflow_before_verifier_reservation(self):
        data = json.loads(self.input.read_text())
        data['taxonomy'] = [{'id': f'c{i:04}', 'name': 'C', 'definition': 'G',
                             'parentId': None if i < 4 else f'c{i % 4:04}'} for i in range(432)]
        self.input.write_text(json.dumps(data))
        with CampaignLedger(self.ledger_path) as ledger:
            campaign = self.controller(ledger)
            for i, payload in enumerate(campaign.payloads):
                response = self.response(payload)
                if i:
                    body = json.loads(response)
                    for key, answer in body['answers'].items():
                        answer['noul'] = .95 if int(key.split('_')[-1]) < 128 else .05
                    response = json.dumps(body).encode()
                campaign.exchange(campaign.names[i], RecordedTransport(response))
            before = ledger.snapshot()['attempts']
            with self.assertRaises(LedgerError):
                campaign.bind_verifier()
            self.assertEqual(ledger.snapshot()['attempts'], before)
            self.assertFalse(any(r.get('event', {}).get('op') == 'verifiers-v2'
                                 for r in ledger.stage_history()))

    def test_partitioned_complete_taxonomy_restart_order_and_result(self):
        with CampaignLedger(self.ledger_path) as ledger:
            campaign, batches = self.large(ledger)
            coverage = [b for b in batches if b['isGlobalCoverage']]
            self.assertGreater(len(coverage), 1)
            observed = [node['id'] for b in coverage
                        for node in json.loads(b['payload'])['state']['taxonomy']]
            self.assertEqual(len(observed), 12)
            self.assertEqual(set(observed), {f'item-{i}' for i in range(12)})
            self.assertLessEqual(len(campaign.payloads) + len(batches), 30)
        for i, batch in enumerate(batches):
            with CampaignLedger(self.ledger_path) as ledger:
                campaign = self.controller(ledger)
                self.assertFalse(campaign.complete)
                transport = RecordedTransport(self.response(batch['payload']))
                with self.assertRaises(LedgerError):
                    campaign.exchange(f'global-coverage:{i + 1}', transport)
                self.assertFalse(transport.used)
                name = f'global-coverage:{i}' if batch['isGlobalCoverage'] else f'verifier:{i}'
                campaign.exchange(name, transport)
                if i + 1 < len(batches):
                    self.assertFalse(campaign.complete)
                    with self.assertRaises(LedgerError):
                        _ = campaign.result
        with CampaignLedger(self.ledger_path) as ledger:
            campaign = self.controller(ledger)
            self.assertTrue(campaign.complete)
            self.assertIsNone(campaign.result['candidate']['failure'])

    def test_malformed_middle_coverage_is_terminal_after_restart(self):
        with CampaignLedger(self.ledger_path) as ledger:
            campaign, batches = self.large(ledger)
            target = next(i for i, b in enumerate(batches) if b['isGlobalCoverage'])
            for i, batch in enumerate(batches[:target]):
                campaign.exchange(f'verifier:{i}', RecordedTransport(self.response(batch['payload'])))
            with self.assertRaises(LedgerError):
                campaign.exchange(f'global-coverage:{target}', RecordedTransport(
                    b'\xef\xbb\xbf' + self.response(batches[target]['payload'])))
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)
        with CampaignLedger(self.ledger_path) as ledger:
            campaign = self.controller(ledger)
            self.assertFalse(campaign.complete)
            transport = RecordedTransport(self.response(batches[target]['payload']))
            with self.assertRaises(LedgerError):
                campaign.exchange(f'global-coverage:{target}', transport)
            self.assertFalse(transport.used)

    def test_oversized_vector_rejected_before_verifier_reservation(self):
        with CampaignLedger(self.ledger_path) as ledger:
            campaign = self.controller(ledger)
            for i, payload in enumerate(campaign.payloads):
                campaign.exchange(campaign.names[i], RecordedTransport(self.response(payload)))
            export = campaign._export_verifiers
            def oversized(done):
                event = export(done)
                event['batches'][0]['questionIds'][0] += 'q' * 524288
                return event
            before = ledger.snapshot()['attempts']
            with patch.object(campaign, '_export_verifiers', side_effect=oversized):
                with self.assertRaisesRegex(LedgerError, 'record size'):
                    campaign.bind_verifier()
            self.assertEqual(ledger.snapshot()['attempts'], before)
            self.assertFalse(any(r.get('event', {}).get('op') == 'verifiers-v2'
                                 for r in ledger.stage_history()))
