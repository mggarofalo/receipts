"""Synthetic local tests only: no live requests, real data or credentials."""
import json
import io
import os
from contextlib import redirect_stderr
from unittest.mock import patch

from campaign_ledger import CampaignLedger, LedgerError, Policy, RESERVATION_NANODOLLARS
from cohort_live_transport import CohortLiveTransport
from offline_coordinator import OfflineCoordinator, UnresolvedStage
from selected_path_cohort import SelectedPathCohort
from selected_path_operator import main as operator_main, publish_results
from test_offline_actual_arms import ActualArmsTests


class SelectedPathCohortTests(ActualArmsTests):
    def cases(self):
        input_data = json.loads(self.input.read_text())
        baseline = self.run_cs('prepare', {'production': '', 'shortlist': [], 'verification': None})
        production = self.reply(baseline['productionPayload'], 'production').decode()
        return [{'id': f'case-{index}', 'input': input_data, 'production': production} for index in range(10)]

    def policy(self, calls=10, carry=123):
        return Policy('selected-synthetic', calls, carry + calls * RESERVATION_NANODOLLARS, carry)

    def cohort(self, ledger, cases, live=None, carry=123):
        return SelectedPathCohort(OfflineCoordinator(ledger, self.work), self.host, self.dll, cases,
                                  prior_spend_nanodollars=carry, live_transport=live)

    def test_frozen_ten_actual_parser_and_shared_budget(self):
        cases = self.cases()
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            cohort = self.cohort(ledger, cases)
            replies = {case['id']: self.reply(plan['payload'], 'selected')
                       for case, plan in zip(cohort.cases, cohort._plans)}
            result = cohort.run(replies)
            self.assertEqual(len(result), 10)
            self.assertTrue(all(value['categoryId'] == 'apple' for value in result.values()))
            self.assertEqual(ledger.snapshot()['attempts'], 10)
            self.assertEqual(cohort.run({}), result)
            self.assertEqual(ledger.snapshot()['attempts'], 10)

    def test_failure_stops_campaign_and_restart_without_retry(self):
        cases = self.cases()
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            cohort = self.cohort(ledger, cases, CohortLiveTransport('SYNTHETIC_KEY'))
            with patch.object(CohortLiveTransport, 'exchange', autospec=True, return_value=b'{}') as send:
                with self.assertRaises(LedgerError):
                    cohort.run({})
                self.assertEqual(send.call_count, 1)
                self.assertEqual(ledger.snapshot()['attempts'], 1)
                with self.assertRaises(UnresolvedStage):
                    cohort.run({})
                self.assertEqual(send.call_count, 1)
        with CampaignLedger(self.ledger_path) as ledger:
            with self.assertRaises(UnresolvedStage):
                self.cohort(ledger, cases, CohortLiveTransport('SYNTHETIC_KEY'))

    def test_unknown_reservation_stops_all_followups(self):
        cases = self.cases()
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            cohort = self.cohort(ledger, cases, CohortLiveTransport('SYNTHETIC_KEY'))
            response = self.reply(cohort._plans[0]['payload'], 'selected')
            with patch.object(CohortLiveTransport, 'exchange', autospec=True, return_value=response) as send:
                with patch.object(cohort.coordinator, '_persist', side_effect=OSError('synthetic')):
                    with self.assertRaises(OSError):
                        cohort.run({})
                with self.assertRaises(UnresolvedStage):
                    cohort.run({})
                self.assertEqual(send.call_count, 1)
                self.assertEqual(ledger.snapshot()['attempts'], 1)

    def test_mock_live_transport_uses_ten_description_only_requests(self):
        cases = self.cases()
        for case in cases:
            case['input']['purchase']['store'] = 'Synthetic shop'
            case['input']['purchase']['productCode'] = 'Synthetic code'
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            cohort = self.cohort(ledger, cases, CohortLiveTransport('SYNTHETIC_KEY'))
            def reply(_transport, payload):
                request = json.loads(payload)
                self.assertIsNone(request['state']['purchase']['store'])
                self.assertIsNone(request['state']['purchase']['productCode'])
                self.assertNotIn('fitProbability', request['state'])
                return self.reply(payload, 'selected')
            with patch.object(CohortLiveTransport, 'exchange', autospec=True, side_effect=reply) as send:
                self.assertEqual(len(cohort.run({})), 10)
                self.assertEqual(send.call_count, 10)
                self.assertEqual(len(cohort.run({})), 10)
                self.assertEqual(send.call_count, 10)

    def test_interrupted_result_publication_recovers_without_resending(self):
        cases = self.cases()
        source = self.work / 'cases.json'
        source.write_text(json.dumps(cases), encoding='utf-8')
        args = ['--private-root', str(self.work), '--input', str(source), '--host', self.host,
                '--dll', str(self.dll), '--key-environment', 'SYNTHETIC_KEY',
                '--budget-nanodollars', str(123 + 10 * RESERVATION_NANODOLLARS),
                '--prior-spend-nanodollars', '123']
        rename = os.rename
        def interrupt_final(source_path, destination_path):
            if str(destination_path).endswith('selected-path-results.json'):
                raise OSError('synthetic interruption')
            return rename(source_path, destination_path)
        def answer(_transport, payload):
            return self.reply(payload, 'selected')
        with patch.object(CohortLiveTransport, 'exchange', autospec=True, side_effect=answer) as send:
            with patch('selected_path_operator.os.rename', side_effect=interrupt_final), redirect_stderr(io.StringIO()):
                self.assertEqual(operator_main([*args, '--create']), 1)
            self.assertFalse((self.work / 'selected-path-results.json').exists())
            self.assertEqual(send.call_count, 10)
            with CampaignLedger(self.work / 'ledger.jsonl') as ledger:
                self.assertEqual(ledger.snapshot()['attempts'], 10)
            (self.work / '.selected-path-results-orphan.partial').write_bytes(b'incomplete')
            self.assertEqual(operator_main(args), 0)
            self.assertEqual(send.call_count, 10)
            self.assertEqual(len(json.loads((self.work / 'selected-path-results.json').read_bytes())), 10)

    def test_publish_never_overwrites_existing_results(self):
        output = self.work / 'selected-path-results.json'
        output.write_bytes(b'original')
        with self.assertRaises(LedgerError):
            publish_results(self.work, b'different')
        self.assertEqual(output.read_bytes(), b'original')

    def test_freeze_budget_labels_and_prior_spend_fail_closed(self):
        cases = self.cases()
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            coordinator = OfflineCoordinator(ledger, self.work)
            with self.assertRaises(LedgerError):
                SelectedPathCohort(coordinator, self.host, self.dll, cases,
                                   prior_spend_nanodollars=0)
            cases[0]['input']['expected'] = 'apple'
            with self.assertRaises(LedgerError):
                self.cohort(ledger, cases)
            del cases[0]['input']['expected']
            cohort = self.cohort(ledger, cases)
            cohort.cases[0]['input']['purchase']['description'] = 'changed'
            with self.assertRaises(LedgerError):
                cohort.run({})
            self.assertEqual(ledger.snapshot()['attempts'], 0)
        with CampaignLedger(self.work / 'other-ledger', create_policy=self.policy(calls=11)) as ledger:
            with self.assertRaises(LedgerError):
                self.cohort(ledger, cases)
        with CampaignLedger(self.work / 'small-ledger', create_policy=Policy(
                'small', 10, 123 + 9 * RESERVATION_NANODOLLARS, 123)) as ledger:
            with self.assertRaises(LedgerError):
                self.cohort(ledger, cases)
