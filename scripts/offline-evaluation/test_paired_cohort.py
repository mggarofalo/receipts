import json
import shutil
from unittest.mock import patch
from campaign_ledger import CampaignLedger, LedgerError, Policy, RESERVATION_NANODOLLARS
from offline_coordinator import OfflineCoordinator, UnresolvedStage
from paired_cohort import CohortIdentityError, PairedCohort
from test_offline_actual_arms import ActualArmsTests

class CohortTests(ActualArmsTests):
    def fixtures(self):
        first = json.loads(self.input.read_text())
        second = json.loads(json.dumps(first))
        second['purchase']['description'] = 'Bought an apple for a snack'
        return [{'id': 'case-a', 'input': first}, {'id': 'case-b', 'input': second}]

    def records(self, cases):
        result = {}
        for case in cases:
            self.input.write_text(json.dumps(case['input']))
            replies = {'production': '', 'shortlist': [], 'verification': None}
            initial = self.run_cs('prepare', replies)
            records = {('baseline', 'production', 0): self.reply(initial['productionPayload'], 'production')}
            for i, payload in enumerate(initial['shortlistPayloads']):
                raw = self.reply(payload, 'shortlist')
                records[('candidate', 'shortlist', i)] = raw
                replies['shortlist'].append(raw.decode())
            dynamic = self.run_cs('prepare', replies)
            if dynamic['verificationPayload'] is not None:
                records[('candidate', 'verification', 0)] = self.reply(dynamic['verificationPayload'], 'verification')
            result[case['id']] = records
        return result

    def test_cohort_restart_and_frozen_inputs(self):
        cases = self.fixtures()
        records = self.records(cases)
        with CampaignLedger(self.ledger_path, create_policy=Policy('cohort', 6, 6 * RESERVATION_NANODOLLARS, 0)) as ledger:
            scheduler = PairedCohort(OfflineCoordinator(ledger, self.work), self.host, self.dll, cases)
            first = scheduler.run_case('case-a', records['case-a'])
            self.assertEqual(first['candidate']['categoryId'], 'apple')
        with CampaignLedger(self.ledger_path) as ledger:
            scheduler = PairedCohort(OfflineCoordinator(ledger, self.work), self.host, self.dll, cases)
            predictions = scheduler.run({'case-b': records['case-b']})
            self.assertEqual(predictions['case-a'], {'status': 'complete', 'predictions': first})
            self.assertEqual(predictions['case-b']['predictions']['baseline']['categoryId'], 'apple')
            self.assertNotIn('Purchased fresh apples', json.dumps(predictions))
            self.assertNotIn('verificationPayload', json.dumps(predictions))
            self.assertEqual(set(predictions['case-a']['predictions']), {'baseline', 'candidate'})
            self.assertEqual(ledger.snapshot()['attempts'], 6)
            self.assertEqual([r['stage_id'] for r in ledger.stage_history() if r['kind'] == 'attempt'],
                [[case, arm, phase, 0] for case in ('case-a', 'case-b') for arm, phase in
                 (('baseline', 'production'), ('candidate', 'shortlist'), ('candidate', 'verification'))])
            cases[0]['input']['purchase']['description'] = 'changed'
            with self.assertRaises(LedgerError):
                PairedCohort(OfflineCoordinator(ledger, self.work), self.host, self.dll, cases)

    def test_unknown_stage_is_not_resent_and_other_case_can_continue(self):
        cases = self.fixtures()
        records = self.records(cases)
        with CampaignLedger(self.ledger_path, create_policy=Policy('unknown', 4, 4 * RESERVATION_NANODOLLARS, 0)) as ledger:
            coordinator = OfflineCoordinator(ledger, self.work)
            scheduler = PairedCohort(coordinator, self.host, self.dll, cases)
            with patch.object(coordinator, '_persist', side_effect=OSError('interruption')):
                with self.assertRaises(OSError):
                    scheduler.run_case('case-a', records['case-a'])
        with CampaignLedger(self.ledger_path) as ledger:
            scheduler = PairedCohort(OfflineCoordinator(ledger, self.work), self.host, self.dll, cases)
            with self.assertRaises(UnresolvedStage):
                scheduler.run_case('case-a', records['case-a'])
            self.assertEqual(scheduler.run_case('case-b', records['case-b'])['candidate']['categoryId'], 'apple')
            self.assertEqual(ledger.snapshot()['attempts'], 4)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)

    def test_missing_response_and_budget_do_not_reserve_extra(self):
        cases = self.fixtures()
        records = self.records(cases)
        with CampaignLedger(self.ledger_path, create_policy=Policy('budget', 3, 3 * RESERVATION_NANODOLLARS, 0)) as ledger:
            scheduler = PairedCohort(OfflineCoordinator(ledger, self.work), self.host, self.dll, cases)
            with self.assertRaises(LedgerError):
                scheduler.run_case('case-a', {})
            self.assertEqual(ledger.snapshot()['attempts'], 0)
            scheduler.run_case('case-a', records['case-a'])
            with self.assertRaises(LedgerError):
                scheduler.run_case('case-b', records['case-b'])
            self.assertEqual(ledger.snapshot()['attempts'], 3)

    def test_run_continues_after_unknown_case(self):
        cases = self.fixtures()
        records = self.records(cases)
        with CampaignLedger(self.ledger_path, create_policy=Policy('continue', 4, 4 * RESERVATION_NANODOLLARS, 0)) as ledger:
            coordinator = OfflineCoordinator(ledger, self.work)
            scheduler = PairedCohort(coordinator, self.host, self.dll, cases)
            with patch.object(coordinator, '_persist', side_effect=OSError('interruption')):
                with self.assertRaises(OSError):
                    scheduler.run_case('case-a', records['case-a'])
            results = scheduler.run(records)
            self.assertEqual(results['case-a'], {'status': 'unresolved'})
            self.assertEqual(results['case-b']['status'], 'complete')
            self.assertEqual(results['case-b']['predictions']['candidate']['categoryId'], 'apple')
            self.assertEqual(ledger.snapshot()['attempts'], 4)

    def test_changed_build_or_plan_fails_before_reusing_stages(self):
        cases = self.fixtures()
        records = self.records(cases)
        copy = self.work / 'build'
        shutil.copytree(self.dll.parent, copy)
        dll = copy / self.dll.name
        with CampaignLedger(self.ledger_path, create_policy=Policy('identity', 6, 6 * RESERVATION_NANODOLLARS, 0)) as ledger:
            scheduler = PairedCohort(OfflineCoordinator(ledger, self.work), self.host, dll, cases)
            scheduler.run_case('case-a', records['case-a'])
            original = scheduler._command
            def changed_plan(command, case, replies):
                value = original(command, case, replies)
                if command == 'prepare':
                    value['planSha256'] = '0' * 64
                return value
            with patch.object(scheduler, '_command', side_effect=changed_plan):
                with self.assertRaises(CohortIdentityError):
                    scheduler.run_case('case-a', {})
            self.assertEqual(ledger.snapshot()['attempts'], 3)
        with (copy / 'extra.json').open('w') as stream:
            stream.write('{}')
        with CampaignLedger(self.ledger_path) as ledger:
            with self.assertRaises(CohortIdentityError):
                PairedCohort(OfflineCoordinator(ledger, self.work), self.host, dll, cases)
            self.assertEqual(ledger.snapshot()['attempts'], 3)
