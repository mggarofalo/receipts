"""Invented 309-category, multi-batch checks against the actual C# runner."""
import json
from unittest.mock import patch
from campaign_ledger import CampaignLedger, LedgerError, Policy, RESERVATION_NANODOLLARS
from offline_coordinator import OfflineCoordinator, UnresolvedStage
from batched_paired_cohort import BatchedPairedCohort, CohortIdentityError, _COHORT_OWNER
from test_offline_actual_arms import ActualArmsTests


class BatchedCohortTests(ActualArmsTests):
    def fixtures_and_records(self):
        taxonomy = [{'id': f'root-{i}', 'name': f'Invented group {i}',
                     'definition': 'A synthetic group', 'parentId': None} for i in range(2)]
        taxonomy += [{'id': f'item-{i:03}', 'name': f'Invented item {i}',
                      'definition': 'x' * 80,
                      'parentId': f'root-{i % 2}'} for i in range(307)]
        case = {'id': 'case-a', 'input': {'purchase': {'description': 'Invented purchase',
            'store': None, 'productCode': None}, 'taxonomyRevision': 'synthetic-309', 'taxonomy': taxonomy}}
        self.input.write_text(json.dumps(case['input']))
        empty = {'production': '', 'shortlist': [], 'verification': []}
        initial = self.run_cs('prepare-batched', empty)
        def reply(payload, phase):
            request = json.loads(payload)
            answers = {}
            for key, question in request['questions'].items():
                if question['type'] == 'noul':
                    value = .95
                    if phase == 'shortlist':
                        category = question['instructions']['path']['categoryId']
                        value = .95 if category.startswith('root-') or (category.startswith('item-') and int(category[5:]) < 40) else .05
                    if phase == 'coverage' and key.endswith('_unlisted'):
                        value = .05
                    answers[key] = {'type': 'noul', 'noul': value}
                else:
                    options = list(question['criteria'])
                    answers[key] = {'type': 'choice', 'choice': options[0], 'confidence': 1,
                                    'probabilities': {option: int(option == options[0]) for option in options}}
            return json.dumps({'model': request['model'], 'answers': answers,
                               'usage': {'input_tokens': 2, 'output_tokens': 1}}).encode()
        records = {('baseline', 'production', 0): reply(initial['productionPayload'], 'production')}
        for i, payload in enumerate(initial['shortlistPayloads']):
            raw = reply(payload, 'shortlist')
            records[('candidate', 'shortlist', i)] = raw
            empty['shortlist'].append(raw.decode())
        dynamic = self.run_cs('prepare-batched', empty)
        batches = dynamic['verificationBatches']
        self.assertGreater(len(initial['shortlistPayloads']), 1)
        self.assertGreaterEqual(sum(not x['isGlobalCoverage'] for x in batches), 2)
        self.assertGreaterEqual(sum(x['isGlobalCoverage'] for x in batches), 2)
        self.assertGreater(sum(key.endswith('_support') for b in batches if not b['isGlobalCoverage']
                               for key in json.loads(b['payload'])['questions']), 32)
        covered = [node['id'] for b in batches if b['isGlobalCoverage']
                   for node in json.loads(b['payload'])['state']['taxonomy']]
        self.assertEqual(len(covered), 309)
        self.assertEqual(set(covered), {node['id'] for node in taxonomy})
        for batch in batches:
            phase = 'coverage' if batch['isGlobalCoverage'] else 'verification'
            records[('candidate', phase, batch['index'])] = reply(batch['payload'], phase)
        return case, initial, batches, records

    def test_actual_multibatch_restart_and_changed_vector(self):
        case, initial, batches, records = self.fixtures_and_records()
        second_dynamic = ('candidate', 'coverage' if batches[1]['isGlobalCoverage'] else 'verification', 1)
        partial = dict(records)
        del partial[second_dynamic]
        with CampaignLedger(self.ledger_path, create_policy=Policy('batched', 30, 30 * RESERVATION_NANODOLLARS, 0)) as ledger:
            scheduler = BatchedPairedCohort(OfflineCoordinator(ledger, self.work), self.host, self.dll, [case], aggregate_call_limit=30, prior_calls=0)
            with self.assertRaises(LedgerError):
                scheduler.run_case('case-a', partial)
            self.assertEqual(ledger.snapshot()['attempts'], 2 + len(initial['shortlistPayloads']))
            original = scheduler._command
            def changed(command, item, replies):
                value = original(command, item, replies)
                if command == 'prepare-batched' and replies['shortlist']:
                    value['verificationBatches'][0]['payload'] += ' '
                return value
            with patch.object(scheduler, '_command', side_effect=changed):
                with self.assertRaises(CohortIdentityError):
                    scheduler.run_case('case-a', records)
            self.assertEqual(ledger.snapshot()['attempts'], 2 + len(initial['shortlistPayloads']))
        with CampaignLedger(self.ledger_path) as ledger:
            scheduler = BatchedPairedCohort(OfflineCoordinator(ledger, self.work), self.host, self.dll, [case], aggregate_call_limit=30, prior_calls=0)
            result = scheduler.run_case('case-a', records)
            self.assertIn(result['candidate']['status'], ('answered', 'abstained'))
            self.assertNotIn('Invented purchase', json.dumps(result))
            attempts = [r['stage_id'] for r in ledger.stage_history() if r['kind'] == 'attempt']
            self.assertEqual(attempts[-len(batches):], [['case-a', 'candidate',
                'coverage' if b['isGlobalCoverage'] else 'verification', b['index']] for b in batches])
            count = len(attempts)
            self.assertEqual(scheduler.run_case('case-a', {}), result)
            self.assertEqual(ledger.snapshot()['attempts'], count)

    def test_prior_carry_blocks_entire_dynamic_vector(self):
        case, initial, batches, records = self.fixtures_and_records()
        initial_count = 1 + len(initial['shortlistPayloads'])
        # Only one dynamic reservation remains after explicitly carried prior cost.
        carry = 2 * RESERVATION_NANODOLLARS
        budget = (initial_count + 3) * RESERVATION_NANODOLLARS
        with CampaignLedger(self.ledger_path, create_policy=Policy('carry', 30, budget, carry)) as ledger:
            scheduler = BatchedPairedCohort(OfflineCoordinator(ledger, self.work), self.host, self.dll, [case], aggregate_call_limit=30, prior_calls=0)
            with self.assertRaises(LedgerError):
                scheduler.run_case('case-a', records)
            self.assertEqual(ledger.snapshot()['attempts'], initial_count)
            self.assertEqual(ledger.snapshot()['reserved_nanodollars'], carry + initial_count * RESERVATION_NANODOLLARS)
            self.assertGreater(len(batches), 1)

    def test_interrupted_dynamic_attempt_is_never_resent(self):
        case, initial, batches, records = self.fixtures_and_records()
        with CampaignLedger(self.ledger_path, create_policy=Policy('interrupt', 30, 30 * RESERVATION_NANODOLLARS, 0)) as ledger:
            coordinator = OfflineCoordinator(ledger, self.work)
            scheduler = BatchedPairedCohort(coordinator, self.host, self.dll, [case], aggregate_call_limit=30, prior_calls=0)
            persist = coordinator._persist
            def interrupt(attempt, response):
                if attempt['stage_id'][2] == 'verification':
                    raise OSError('synthetic interruption')
                return persist(attempt, response)
            with patch.object(coordinator, '_persist', side_effect=interrupt):
                with self.assertRaises(OSError):
                    scheduler.run_case('case-a', records)
            attempts = ledger.snapshot()['attempts']
        with CampaignLedger(self.ledger_path) as ledger:
            scheduler = BatchedPairedCohort(OfflineCoordinator(ledger, self.work), self.host, self.dll, [case], aggregate_call_limit=30, prior_calls=0)
            with self.assertRaises(UnresolvedStage):
                scheduler.run_case('case-a', records)
            self.assertEqual(ledger.snapshot()['attempts'], attempts)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)


    def small_cases_and_records(self):
        input_data = json.loads(self.input.read_text())
        cases = [{'id': 'case-a', 'input': input_data}, {'id': 'case-b', 'input': input_data}]
        replies = {'production': '', 'shortlist': [], 'verification': []}
        initial = self.run_cs('prepare-batched', replies)
        records = {('baseline', 'production', 0): self.reply(initial['productionPayload'], 'production')}
        for index, payload in enumerate(initial['shortlistPayloads']):
            raw = self.reply(payload, 'shortlist')
            records[('candidate', 'shortlist', index)] = raw
            replies['shortlist'].append(raw.decode())
        dynamic = self.run_cs('prepare-batched', replies)
        for batch in dynamic['verificationBatches']:
            phase = 'coverage' if batch['isGlobalCoverage'] else 'verification'
            records[('candidate', phase, batch['index'])] = self.reply(batch['payload'], 'verification')
        self.assertEqual(len(records), 4)
        return cases, records

    def test_multicase_shared_call_allowance_and_process_lock(self):
        cases, records = self.small_cases_and_records()
        carry = 2 * RESERVATION_NANODOLLARS
        policy = Policy('shared', 6, 8 * RESERVATION_NANODOLLARS, carry)
        with CampaignLedger(self.ledger_path, create_policy=policy) as ledger:
            coordinator = OfflineCoordinator(ledger, self.work)
            with self.assertRaises(LedgerError):
                BatchedPairedCohort(coordinator, self.host, self.dll, cases, aggregate_call_limit=7, prior_calls=2)
            self.assertEqual(ledger.snapshot()['attempts'], 0)
            scheduler = BatchedPairedCohort(coordinator, self.host, self.dll, cases, aggregate_call_limit=8, prior_calls=2)
            _COHORT_OWNER.acquire()
            try:
                with self.assertRaises(LedgerError):
                    scheduler.run_case('case-a', records)
                self.assertEqual(ledger.snapshot()['attempts'], 0)
            finally:
                _COHORT_OWNER.release()
            results = scheduler.run({'case-a': records, 'case-b': records})
            self.assertEqual(results['case-a']['status'], 'complete')
            self.assertEqual(results['case-b'], {'status': 'unresolved'})
            self.assertEqual(ledger.snapshot()['attempts'], 6)
            self.assertEqual(ledger.snapshot()['reserved_nanodollars'], 8 * RESERVATION_NANODOLLARS)
            with self.assertRaises(CohortIdentityError):
                BatchedPairedCohort(coordinator, self.host, self.dll, cases, aggregate_call_limit=9, prior_calls=2)

    def test_unknown_case_allows_independent_case_and_counts_prior(self):
        cases, records = self.small_cases_and_records()
        carry = 2 * RESERVATION_NANODOLLARS
        with CampaignLedger(self.ledger_path, create_policy=Policy('unknown-shared', 6,
                8 * RESERVATION_NANODOLLARS, carry)) as ledger:
            coordinator = OfflineCoordinator(ledger, self.work)
            scheduler = BatchedPairedCohort(coordinator, self.host, self.dll, cases, aggregate_call_limit=8, prior_calls=2)
            persist = coordinator._persist
            def interrupt(attempt, response):
                if attempt['stage_id'] == ['case-a', 'baseline', 'production', 0]:
                    raise OSError('synthetic interruption')
                return persist(attempt, response)
            with patch.object(coordinator, '_persist', side_effect=interrupt):
                result = scheduler.run({'case-a': records, 'case-b': records})
            self.assertEqual(result['case-a'], {'status': 'unresolved'})
            self.assertEqual(result['case-b']['status'], 'complete')
            self.assertEqual(ledger.snapshot()['attempts'], 5)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)
            with self.assertRaises(UnresolvedStage):
                scheduler.run_case('case-a', records)
            self.assertEqual(ledger.snapshot()['attempts'], 5)

    def test_prior_call_carry_must_not_be_refunded(self):
        cases, _ = self.small_cases_and_records()
        with CampaignLedger(self.ledger_path, create_policy=Policy('carry-short', 6,
                8 * RESERVATION_NANODOLLARS, RESERVATION_NANODOLLARS)) as ledger:
            with self.assertRaises(LedgerError):
                BatchedPairedCohort(OfflineCoordinator(ledger, self.work), self.host, self.dll, cases,
                                    aggregate_call_limit=8, prior_calls=2)
            self.assertEqual(ledger.snapshot()['attempts'], 0)
