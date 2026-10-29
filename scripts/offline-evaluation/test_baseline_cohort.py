import json
import hashlib
import os
import subprocess
from unittest.mock import patch

from baseline_cohort import BaselineCohort
from campaign_ledger import CampaignLedger, LedgerError, Policy, RESERVATION_NANODOLLARS
from cohort_live_transport import CohortLiveTransport
from offline_coordinator import OfflineCoordinator, UnresolvedStage
from test_offline_actual_arms import ActualArmsTests


class BaselineCohortTests(ActualArmsTests):
    def setUp(self):
        super().setUp()
        self.freeze = self.work / 'freeze.json'
        self.freeze.write_text('{"synthetic":true}')

    def cohort(self, coordinator, cases):
        return BaselineCohort(coordinator, self.host, self.dll, cases,
                              freeze_path=self.freeze,
                              freeze_sha256=hashlib.sha256(self.freeze.read_bytes()).hexdigest())

    def fixtures(self):
        first = json.loads(self.input.read_text())
        second = json.loads(json.dumps(first))
        second['purchase']['description'] = 'Bought an apple for a snack'
        return [{'id': 'case-a', 'input': first}, {'id': 'case-b', 'input': second}]

    def replies(self, cases):
        result = {}
        for case in cases:
            self.input.write_text(json.dumps(case['input']))
            plan = self.run_cs('prepare', {'production': '', 'shortlist': [], 'verification': None})
            result[case['id']] = self.reply(plan['productionPayload'], 'production')
        return result

    def policy(self, calls=2, carry=650_000_000):
        return Policy('baseline-fixture', calls, carry + calls * RESERVATION_NANODOLLARS, carry)

    def test_actual_baseline_shared_ledger_and_restart(self):
        cases = self.fixtures()
        replies = self.replies(cases)
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            cohort = self.cohort(OfflineCoordinator(ledger, self.work), cases)
            with self.assertRaises(LedgerError):
                cohort.run_case('case-a', {})
            result = cohort.run(replies)
            self.assertEqual([v['categoryId'] for v in result.values()], ['apple', 'apple'])
            self.assertEqual(ledger.snapshot()['attempts'], 2)
        with CampaignLedger(self.ledger_path) as ledger:
            cohort = self.cohort(OfflineCoordinator(ledger, self.work), cases)
            self.assertEqual(cohort.run({}), result)
            self.assertEqual(ledger.snapshot()['attempts'], 2)
            cases[0]['input']['purchase']['description'] = 'changed'
            with self.assertRaises(LedgerError):
                self.cohort(OfflineCoordinator(ledger, self.work), cases)

    def test_unknown_stops_entire_cohort_and_never_retries(self):
        cases = self.fixtures()
        replies = self.replies(cases)
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            coordinator = OfflineCoordinator(ledger, self.work)
            cohort = self.cohort(coordinator, cases)
            with patch.object(coordinator, '_persist', side_effect=OSError('synthetic')):
                with self.assertRaises(OSError):
                    cohort.run(replies)
            with self.assertRaises(UnresolvedStage):
                cohort.run(replies)
            self.assertEqual(ledger.snapshot()['attempts'], 1)
        with CampaignLedger(self.ledger_path) as ledger:
            with self.assertRaises(UnresolvedStage):
                self.cohort(OfflineCoordinator(ledger, self.work), cases)
            self.assertEqual(ledger.snapshot()['attempts'], 1)

    def test_invalid_response_and_limits_fail_closed(self):
        cases = self.fixtures()
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            cohort = self.cohort(OfflineCoordinator(ledger, self.work), cases)
            with self.assertRaises(LedgerError):
                cohort.run({'case-a': b'{}'})
            self.assertEqual(ledger.snapshot()['attempts'], 1)
            with self.assertRaises(UnresolvedStage):
                cohort.run({})
        other = self.work / 'other-ledger'
        with CampaignLedger(other, create_policy=self.policy(carry=0)) as ledger:
            with self.assertRaises(LedgerError):
                self.cohort(OfflineCoordinator(ledger, self.work), cases)
            self.assertEqual(ledger.snapshot()['attempts'], 0)

    def test_freeze_drift_and_label_in_input_rejected_before_dispatch(self):
        cases = self.fixtures()
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            cohort = self.cohort(OfflineCoordinator(ledger, self.work), cases)
            self.freeze.write_text('{"synthetic":false}')
            with self.assertRaises(LedgerError):
                cohort.run(self.replies(cases))
            self.assertEqual(ledger.snapshot()['attempts'], 0)
        other = self.work / 'label-ledger'
        cases[0]['input']['expected'] = 'fictional'
        with CampaignLedger(other, create_policy=self.policy()) as ledger:
            with self.assertRaises(LedgerError):
                self.cohort(OfflineCoordinator(ledger, self.work), cases)
            self.assertEqual(ledger.snapshot()['attempts'], 0)

    def test_mutable_case_id_and_order_cannot_change_before_dispatch(self):
        cases = self.fixtures()
        replies = self.replies(cases)
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            cohort = self.cohort(OfflineCoordinator(ledger, self.work), cases)
            cohort.cases[0]['id'] = 'other'
            with self.assertRaises(LedgerError):
                cohort.run(replies)
            self.assertEqual(ledger.snapshot()['attempts'], 0)
            cohort.cases[0]['id'] = 'case-a'
            cohort.cases.reverse()
            with self.assertRaises(LedgerError):
                cohort.run(replies)
            self.assertEqual(ledger.snapshot()['attempts'], 0)

    def test_transport_change_is_rejected_before_dispatch(self):
        cases = self.fixtures()
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            cohort = self.cohort(OfflineCoordinator(ledger, self.work), cases)
            cohort.live_transport = CohortLiveTransport('SYNTHETIC_KEY')
            with self.assertRaises(LedgerError):
                cohort.run({})
            self.assertEqual(ledger.snapshot()['attempts'], 0)
        live_root = self.work / 'live-root'
        live_root.mkdir()
        with CampaignLedger(live_root / 'ledger', create_policy=self.policy()) as ledger:
            transport = CohortLiveTransport('SYNTHETIC_KEY')
            cohort = BaselineCohort(OfflineCoordinator(ledger, live_root), self.host, self.dll, cases,
                                    freeze_path=self.freeze, freeze_sha256=hashlib.sha256(self.freeze.read_bytes()).hexdigest(),
                                    live_transport=transport)
            transport.key_environment = 'OTHER_SYNTHETIC_KEY'
            with self.assertRaises(LedgerError):
                cohort.run({})
            self.assertEqual(ledger.snapshot()['attempts'], 0)

    def test_offline_child_receives_only_safe_environment_before_reservation(self):
        cases = self.fixtures()
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            with patch.dict(os.environ, {'TYPESAFE_API_KEY': 'synthetic-secret', 'DOTNET_STARTUP_HOOKS': 'synthetic-hook'}):
                with patch('baseline_cohort.subprocess.run', wraps=subprocess.run) as child:
                    self.cohort(OfflineCoordinator(ledger, self.work), cases)
                    self.assertTrue(child.called)
                    for call in child.call_args_list:
                        self.assertEqual(set(call.kwargs['env']), {'SystemRoot'})
                        self.assertEqual(ledger.snapshot()['attempts'], 0)

    def test_baseline_result_rejects_missing_or_malformed_reply(self):
        for replies in ({'production': '', 'shortlist': [], 'verification': None},
                        {'production': '{}', 'shortlist': [], 'verification': None},
                        {'production': '{}', 'shortlist': ['{}'], 'verification': None},
                        {'production': '{}', 'shortlist': [], 'verification': '{}'}):
            with self.assertRaises(LedgerError):
                self.run_cs('baseline-result', replies)

    def test_valid_production_abstention(self):
        cases = self.fixtures()
        replies = self.replies(cases)
        response = json.loads(replies['case-a'])
        response['answers']['fits']['noul'] = 0.01
        replies['case-a'] = json.dumps(response).encode()
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            cohort = self.cohort(OfflineCoordinator(ledger, self.work), cases)
            result = cohort.run(replies)
            self.assertEqual(result['case-a'], {'status': 'abstained', 'categoryId': None})
            self.assertEqual(ledger.snapshot()['attempts'], 2)

    def test_transport_mutation_during_prepare_stops_before_reservation(self):
        cases = self.fixtures()
        transport = CohortLiveTransport('SYNTHETIC_KEY')
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            cohort = BaselineCohort(OfflineCoordinator(ledger, self.work), self.host, self.dll, cases,
                                    freeze_path=self.freeze, freeze_sha256=hashlib.sha256(self.freeze.read_bytes()).hexdigest(),
                                    live_transport=transport)
            command = cohort._command
            def mutate(command_name, case, replies):
                result = command(command_name, case, replies)
                if command_name == 'prepare':
                    transport.key_environment = 'OTHER_SYNTHETIC_KEY'
                return result
            with patch.object(cohort, '_command', side_effect=mutate), patch.object(CohortLiveTransport, 'exchange') as exchange:
                with self.assertRaises(LedgerError):
                    cohort.run({})
                exchange.assert_not_called()
            self.assertEqual(ledger.snapshot()['attempts'], 0)

    def test_case_mutation_during_prepare_stops_before_reservation(self):
        cases = self.fixtures()
        replies = self.replies(cases)
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            cohort = self.cohort(OfflineCoordinator(ledger, self.work), cases)
            command = cohort._command
            def mutate(command_name, case, reply):
                result = command(command_name, case, reply)
                if command_name == 'prepare':
                    cohort.cases[0]['id'] = 'other'
                return result
            with patch.object(cohort, '_command', side_effect=mutate):
                with self.assertRaises(LedgerError):
                    cohort.run(replies)
            self.assertEqual(ledger.snapshot()['attempts'], 0)
