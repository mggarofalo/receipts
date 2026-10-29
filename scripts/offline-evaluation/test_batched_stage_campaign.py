"""Synthetic real frozen C# integration; recorded responses only, no network."""
import json
import os
import unittest
from unittest.mock import patch

from batched_stage_campaign import BatchedPairedStageCampaign
from campaign_ledger import CampaignLedger, LedgerError, _canonical, RECORD_BYTE_CAPS
from guarded_exchange import RecordedTransport
import test_paired_stage_campaign as paired_fixture
import test_bounded_ledger_capacity as capacity_fixture
from test_bounded_ledger_capacity import encode


@unittest.skipUnless(os.name == 'nt', 'Windows ledger and frozen runtime')
class ActualBatchedStageTests(unittest.TestCase):
    setUpClass = classmethod(paired_fixture.ActualPairedStageTests.setUpClass.__func__)
    def setUp(self):
        paired_fixture.ActualPairedStageTests.setUp(self)
        self.ledger_path.unlink()
        from campaign_ledger import Policy
        with CampaignLedger(self.ledger_path, create_policy=Policy('actual-stage', 96, 5_000_000_000, 0)):
            pass
    response = paired_fixture.ActualPairedStageTests.response

    def controller(self, ledger):
        return BatchedPairedStageCampaign(ledger, self.runner, self.input,
            case='invented-apples', private_root=self.private)

    def prepare(self, ledger, leaves=33):
        data = json.loads(self.input.read_text())
        data['taxonomy'] = [{'id': f'item-{i}', 'name': f'Fruit {i}',
            'definition': f'Invented fruit {i}', 'parentId': None} for i in range(leaves)]
        self.input.write_text(json.dumps(data))
        controller = self.controller(ledger)
        for i, payload in enumerate(controller.payloads):
            controller.exchange(controller.names[i], RecordedTransport(self.response(payload)))
        controller.bind_verifier()
        return controller, ledger.stage_history()[-1]['event']['batches']

    def test_actual_complete_vector_restart_order_and_result(self):
        with CampaignLedger(self.ledger_path) as ledger:
            controller, batches = self.prepare(ledger)
            self.assertGreaterEqual(len(batches), 3)
            self.assertTrue(batches[-1]['isGlobalCoverage'])
            with self.assertRaises(LedgerError):
                _ = controller.result
            for name in ('global-coverage', 'verifier:1'):
                transport = RecordedTransport(self.response(batches[-1]['payload']))
                with self.assertRaises(LedgerError):
                    controller.exchange(name, transport)
                self.assertFalse(transport.used)
        for index, batch in enumerate(batches):
            with CampaignLedger(self.ledger_path) as ledger:
                controller = self.controller(ledger)
                self.assertFalse(controller.complete)
                controller.exchange('global-coverage' if batch['isGlobalCoverage'] else f'verifier:{index}',
                                    RecordedTransport(self.response(batch['payload'])))
                if index < len(batches) - 1:
                    with self.assertRaises(LedgerError):
                        _ = controller.result
        with CampaignLedger(self.ledger_path) as ledger:
            controller = self.controller(ledger)
            self.assertTrue(controller.complete)
            self.assertIsNone(controller.result['candidate']['failure'])
            with self.assertRaises(LedgerError):
                controller.exchange('verifier:0', RecordedTransport(self.response(batches[0]['payload'])))

    def _final_coverage_failure(self, aggregation=False):
        with CampaignLedger(self.ledger_path) as ledger:
            controller, batches = self.prepare(ledger, leaves=2)
            controller.exchange('verifier:0', RecordedTransport(self.response(batches[0]['payload'])))
            response = self.response(batches[-1]['payload'])
            if not aggregation:
                # Envelope accepts UTF-8 BOM; the actual frozen parser rejects it.
                response = b'\xef\xbb\xbf' + response
            original = controller._run
            commands = []
            def run(command, replies):
                result = original(command, replies)
                commands.append(command)
                if aggregation and command == 'replay-batched':
                    result['candidate']['failure'] = 'synthetic-final-aggregation-failure'
                return result
            before = ledger.snapshot()['attempts']
            with patch.object(controller, '_run', side_effect=run):
                with self.assertRaises(LedgerError):
                    controller.exchange('global-coverage', RecordedTransport(response))
            if aggregation:
                self.assertIn('validate-batched', commands)
                self.assertIn('replay-batched', commands)
            self.assertEqual(ledger.snapshot()['attempts'], before + 1)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)
            history = ledger.stage_history()
            self.assertEqual(history[-1]['kind'], 'attempt')
        with CampaignLedger(self.ledger_path) as ledger:
            controller = self.controller(ledger)
            self.assertFalse(controller.complete)
            transport = RecordedTransport(self.response(batches[-1]['payload']))
            with self.assertRaises(LedgerError):
                controller.exchange('global-coverage', transport)
            self.assertFalse(transport.used)
            with self.assertRaises(LedgerError):
                _ = controller.result

    def test_malformed_final_coverage_before_success_is_terminal(self):
        self._final_coverage_failure()

    def test_final_aggregation_failure_before_success_is_terminal(self):
        self._final_coverage_failure(aggregation=True)

    def test_actual_verifier_parser_failure_is_terminal_after_restart(self):
        with CampaignLedger(self.ledger_path) as ledger:
            controller, batches = self.prepare(ledger, leaves=2)
            response = b'\xef\xbb\xbf' + self.response(batches[0]['payload'])
            with self.assertRaises(LedgerError):
                controller.exchange('verifier:0', RecordedTransport(response))
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)
        with CampaignLedger(self.ledger_path) as ledger:
            controller = self.controller(ledger)
            with self.assertRaises(LedgerError):
                controller.exchange('verifier:0', RecordedTransport(self.response(batches[0]['payload'])))
            with self.assertRaises(LedgerError):
                _ = controller.result

    def test_crash_after_verification_success_before_finish_is_terminal(self):
        with CampaignLedger(self.ledger_path) as ledger:
            controller, batches = self.prepare(ledger, leaves=2)
            with patch.object(controller, '_finish', side_effect=RuntimeError('synthetic crash')):
                with self.assertRaises(RuntimeError):
                    controller.exchange('verifier:0', RecordedTransport(self.response(batches[0]['payload'])))
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 0)
        with CampaignLedger(self.ledger_path) as ledger:
            controller = self.controller(ledger)
            self.assertFalse(controller.complete)
            with self.assertRaises(LedgerError):
                controller.exchange('verifier:0', RecordedTransport(self.response(batches[0]['payload'])))

    def test_binding_cannot_be_early_duplicate_or_cross_plan(self):
        with CampaignLedger(self.ledger_path) as ledger:
            controller = self.controller(ledger)
            with self.assertRaises(LedgerError):
                controller.bind_verifier()
            for i, payload in enumerate(controller.payloads):
                controller.exchange(controller.names[i], RecordedTransport(self.response(payload)))
            controller.bind_verifier()
            with self.assertRaises(LedgerError):
                controller.bind_verifier()
        bodies = [json.loads(row)['body'] for row in self.ledger_path.read_bytes().splitlines()]
        original = encode(bodies)
        for damage in ('cross-plan', 'payload', 'reordered', 'truncated', 'duplicate'):
            with self.subTest(damage=damage):
                bodies = [json.loads(row)['body'] for row in original.splitlines()]
                event = bodies[-1]['event']
                if damage == 'cross-plan':
                    event['plan_sha256'] = 'f' * 64
                elif damage == 'payload':
                    event['batches'][0]['payload'] += ' '
                elif damage == 'reordered':
                    event['batches'].reverse()
                elif damage == 'truncated':
                    event['batches'].pop()
                else:
                    event['batches'].insert(0, event['batches'][0])
                self.ledger_path.write_bytes(encode(bodies))
                with self.assertRaises(LedgerError):
                    with CampaignLedger(self.ledger_path) as ledger:
                        self.controller(ledger)

    def test_actual_abstention_no_verification_and_no_early_result(self):
        with CampaignLedger(self.ledger_path) as ledger:
            controller = self.controller(ledger)
            for i, payload in enumerate(controller.payloads):
                controller.exchange(controller.names[i], RecordedTransport(self.response(payload, 0 if i else .95)))
            with self.assertRaises(LedgerError):
                _ = controller.result
            controller.bind_verifier()
            self.assertTrue(controller.complete)
            self.assertIsNone(controller.result['verificationSha256'])


@unittest.skipUnless(os.name == 'nt', 'Windows ledger')
class BatchedCapacityTests(unittest.TestCase):
    setUp = capacity_fixture.BoundedCapacityTests.setUp
    policy = capacity_fixture.BoundedCapacityTests.policy
    bodies = capacity_fixture.BoundedCapacityTests.bodies

    def event(self):
        return {'op': 'verifiers', 'batches': [
            {'index': i, 'isGlobalCoverage': i == 4, 'payload': 'x' * 32768,
             'questionIds': [f'q-{i}']} for i in range(5)],
            'plan_sha256': 'a' * 64, 'responses': ['b' * 64] * 90}

    def test_five_batches_share_cap_and_reopen_96_call_capacity(self):
        with CampaignLedger(self.path, create_policy=self.policy()) as ledger:
            ledger.append_stage_event(self.event())
            for _ in range(96):
                attempt = ledger.reserve_attempt(b'{}')
                ledger.record_outcome(attempt, 'success')
        with CampaignLedger(self.path) as ledger:
            self.assertEqual(ledger.snapshot()['attempts'], 96)
            self.assertEqual(len(ledger.stage_history()[0]['event']['batches']), 5)

    def test_exact_aggregate_record_cap_and_one_byte_overflow_on_append_and_reopen(self):
        for excess in (0, 1):
            with self.subTest(excess=excess):
                self.path.unlink(missing_ok=True)
                with CampaignLedger(self.path, create_policy=self.policy()) as ledger:
                    event = self.event()
                    body = {'kind': 'stage', 'event': event}
                    header = self.bodies()[0]
                    size = len(encode([header, body])) - self.path.stat().st_size
                    event['batches'][0]['questionIds'][0] += 'q' * (RECORD_BYTE_CAPS['verifiers'] - size + excess)
                    before = self.path.read_bytes()
                    if excess:
                        with self.assertRaisesRegex(LedgerError, 'record size'):
                            ledger.append_stage_event(event)
                        self.assertEqual(self.path.read_bytes(), before)
                    else:
                        ledger.append_stage_event(event)
                        self.assertEqual(len(self.path.read_bytes().splitlines(keepends=True)[-1]), 524288)
                if excess:
                    self.path.write_bytes(encode([header, body]))
                    with self.assertRaises(LedgerError):
                        CampaignLedger(self.path)
                else:
                    with CampaignLedger(self.path) as ledger:
                        self.assertEqual(len(ledger.stage_history()), 1)

    def test_aggregate_overflow_and_invalid_vectors_rejected_without_write(self):
        with CampaignLedger(self.path, create_policy=self.policy()) as ledger:
            event = self.event()
            event['batches'][0]['questionIds'] = ['q' * 524288]
            before = self.path.read_bytes()
            with self.assertRaisesRegex(LedgerError, 'record size'):
                ledger.append_stage_event(event)
            self.assertEqual(self.path.read_bytes(), before)
            for count in (1, 6):
                event = self.event()
                event['batches'] = [event['batches'][0]] * count
                with self.assertRaises(LedgerError):
                    ledger.append_stage_event(event)
            with patch('campaign_ledger.MAX_LEDGER_BYTES', 1902591):
                with self.assertRaisesRegex(LedgerError, 'insufficient journal capacity'):
                    ledger.append_stage_event({'op': 'start', 'index': 0, 'request_sha256': 'a' * 64})
            self.assertEqual(self.path.read_bytes(), before)
