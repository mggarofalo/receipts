import json
from unittest.mock import patch
from campaign_ledger import CampaignLedger, LedgerError, Policy, RESERVATION_NANODOLLARS
from cohort_live_transport import CohortLiveTransport
from holdout_cohort import HoldoutCohort
from offline_coordinator import OfflineCoordinator, UnresolvedStage
from test_offline_actual_arms import ActualArmsTests

class HoldoutTests(ActualArmsTests):
    def cases(self):
        return [{'id': f'case-{i:02d}', 'input': json.loads(self.input.read_text())} for i in range(12)]
    def cohort(self, ledger, cases):
        return HoldoutCohort(OfflineCoordinator(ledger, self.work), self.host, self.dll, cases,
                             prior_spend_nanodollars=123, live_transport=CohortLiveTransport('SYNTHETIC_KEY'))
    def policy(self):
        return Policy('synthetic-holdout', 24, 123 + 24 * RESERVATION_NANODOLLARS, 123)
    def test_mixed_predictions_shared_budget_and_restart(self):
        calls = []
        def answer(_, payload):
            request = json.loads(payload)
            selected = len(request['questions']) == 1
            calls.append(selected)
            reply = json.loads(self.reply(payload, 'selected' if selected else 'production'))
            if not selected:
                reply['answers']['fits']['noul'] = .2 if calls.count(False) % 2 == 0 else .7
            else:
                self.assertIsNone(request['state']['purchase']['store'])
                self.assertNotIn('fitProbability', request['state'])
                next(iter(reply['answers'].values()))['noul'] = .7
            return json.dumps(reply).encode()
        cases = self.cases()
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            cohort = self.cohort(ledger, cases)
            with patch.object(CohortLiveTransport, 'exchange', autospec=True, side_effect=answer):
                result = cohort.run({})
                self.assertEqual(len(calls), 18)
                self.assertEqual(ledger.snapshot()['attempts'], 18)
                for i, case in enumerate(cases):
                    prediction = result[case['id']]
                    self.assertEqual(prediction['baseline']['status'], 'answered' if i % 2 == 0 else 'abstained')
                    self.assertEqual(prediction['selected'], prediction['baseline'])
                self.assertEqual(cohort.run({}), result)
                self.assertEqual(len(calls), 18)
        with CampaignLedger(self.ledger_path) as ledger:
            with patch.object(CohortLiveTransport, 'exchange', autospec=True, side_effect=AssertionError('resend')):
                self.assertEqual(self.cohort(ledger, cases).run({}), result)
    def test_failure_durable_stop_and_no_retry(self):
        cases = self.cases()
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            cohort = self.cohort(ledger, cases)
            with patch.object(CohortLiveTransport, 'exchange', autospec=True, return_value=b'{}') as send:
                with self.assertRaises(Exception):
                    cohort.run({})
                with self.assertRaises(UnresolvedStage):
                    cohort.run({})
                self.assertEqual(send.call_count, 1)
                self.assertEqual(ledger.snapshot()['attempts'], 1)
        with CampaignLedger(self.ledger_path) as ledger:
            with self.assertRaises(UnresolvedStage):
                self.cohort(ledger, cases)
    def test_labels_and_policy_rejected(self):
        cases = self.cases()
        cases[0]['input']['label'] = 'apple'
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            with self.assertRaises(LedgerError):
                self.cohort(ledger, cases)
            self.assertEqual(ledger.snapshot()['attempts'], 0)
    def test_all_answered_and_selected_gate(self):
        count = 0
        def answer(_, payload):
            nonlocal count
            request = json.loads(payload)
            selected = len(request['questions']) == 1
            reply = json.loads(self.reply(payload, 'selected' if selected else 'production'))
            if selected:
                count += 1
                next(iter(reply['answers'].values()))['noul'] = .69 if count % 2 == 0 else .7
            return json.dumps(reply).encode()
        cases = self.cases()
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            with patch.object(CohortLiveTransport, 'exchange', autospec=True, side_effect=answer) as send:
                result = self.cohort(ledger, cases).run({})
                self.assertEqual(send.call_count, 24)
                self.assertEqual(ledger.snapshot()['attempts'], 24)
                for index, case in enumerate(cases):
                    self.assertEqual(result[case['id']]['selected']['status'], 'answered' if index % 2 == 0 else 'abstained')
                self.assertEqual(json.loads((self.work / 'holdout-predictions.json').read_bytes()), result)
    def test_unknown_reservation_stops_all_future_calls(self):
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            cohort = self.cohort(ledger, self.cases())
            with patch.object(CohortLiveTransport, 'exchange', autospec=True, return_value=b'{}') as send:
                with patch.object(cohort.coordinator, '_persist', side_effect=OSError('interrupted')):
                    with self.assertRaises(OSError):
                        cohort.run({})
                with self.assertRaises(UnresolvedStage):
                    cohort.run({})
                self.assertEqual(send.call_count, 1)
    def test_new_ledger_same_policy_is_rejected_even_at_original_path(self):
        cases = self.cases()
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            self.cohort(ledger, cases)
        with CampaignLedger(self.work / 'other-ledger', create_policy=self.policy()) as other:
            with self.assertRaises(LedgerError):
                self.cohort(other, cases)
            self.assertEqual(other.snapshot()['attempts'], 0)
        self.ledger_path.rename(self.work / 'original-ledger')
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as replacement:
            with self.assertRaises(LedgerError):
                self.cohort(replacement, cases)
            self.assertEqual(replacement.snapshot()['attempts'], 0)

    def test_changed_managed_output_prevents_dispatch(self):
        import shutil
        output = self.work / 'runner'
        shutil.copytree(self.dll.parent, output)
        dll = output / self.dll.name
        dependency = output / 'identity-probe.dll'
        dependency.write_bytes(b'synthetic initial managed output')
        with CampaignLedger(self.ledger_path, create_policy=self.policy()) as ledger:
            cohort = HoldoutCohort(OfflineCoordinator(ledger, self.work), self.host, dll, self.cases(),
                                   prior_spend_nanodollars=123, live_transport=CohortLiveTransport('SYNTHETIC_KEY'))
            dependency.write_bytes(b'synthetic changed managed output')
            with patch.object(CohortLiveTransport, 'exchange', autospec=True) as send:
                with self.assertRaises(LedgerError):
                    cohort.run({})
                send.assert_not_called()
                self.assertEqual(ledger.snapshot()['attempts'], 0)
