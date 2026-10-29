import os
import subprocess
from unittest.mock import patch
from campaign_ledger import CampaignLedger, LedgerError, Policy, RESERVATION_NANODOLLARS
from cohort_live_transport import CohortLiveTransport
from offline_coordinator import OfflineCoordinator, UnresolvedStage
from test_offline_coordinator import CoordinatorTests

class LiveCoordinatorTests(CoordinatorTests):
    def test_reserve_before_send_and_reparse(self):
        with CampaignLedger(self.path) as ledger:
            runner = OfflineCoordinator(ledger, self.root)
            transport = CohortLiveTransport('TEST_COHORT_KEY')
            def fake(*args, **kwargs):
                self.assertEqual(ledger.snapshot()['attempts'], 1)
                self.assertEqual(kwargs['env']['COHORT_WORKER_KEY'], 'fake-secret')
                self.assertEqual(kwargs['timeout'], 30)
                return subprocess.CompletedProcess(args[0], 0, b'42')
            with patch.dict(os.environ, {'TEST_COHORT_KEY': 'fake-secret'}), patch('cohort_live_transport.subprocess.run', side_effect=fake) as send:
                with self.assertRaises(ValueError):
                    runner.execute(self.a, b'payload', transport, lambda raw: int('invalid'))
                self.assertEqual(runner.execute(self.a, b'payload', transport, int), 42)
                self.assertEqual(send.call_count, 1)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 0)
            self.assertNotIn('fake-secret', str(ledger.stage_history()))

    def test_failure_is_redacted_and_never_resent(self):
        with CampaignLedger(self.path) as ledger:
            runner = OfflineCoordinator(ledger, self.root)
            transport = CohortLiveTransport('TEST_COHORT_KEY')
            with patch.dict(os.environ, {'TEST_COHORT_KEY': 'fake-secret'}), patch('cohort_live_transport.subprocess.run', side_effect=OSError('fake-secret')) as send:
                with self.assertRaises(LedgerError) as error:
                    runner.execute(self.a, b'payload', transport, bytes)
                self.assertNotIn('fake-secret', str(error.exception))
                with self.assertRaises(UnresolvedStage):
                    runner.execute(self.a, b'payload', transport, bytes)
                self.assertEqual(send.call_count, 1)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)

from test_paired_cohort import CohortTests
from paired_cohort import PairedCohort

class LiveCohortIntegrationTests(CohortTests):
    def test_live_stages_use_shared_ledger_and_do_not_resend(self):
        cases = self.fixtures()[:1]
        records = self.records(cases)['case-a']
        responses = iter(records.values())
        with CampaignLedger(self.ledger_path, create_policy=Policy('live-cohort', 3, 3 * RESERVATION_NANODOLLARS, 0)) as ledger:
            cohort = PairedCohort(OfflineCoordinator(ledger, self.work), self.host, self.dll, cases,
                                  live_transport=CohortLiveTransport('TEST_COHORT_KEY'))
            def fake(_transport, _payload):
                self.assertEqual(ledger.snapshot()['attempts'], send.call_count)
                return next(responses)
            with patch.object(CohortLiveTransport, 'exchange', autospec=True, side_effect=fake) as send:
                first = cohort.run_case('case-a', {})
                self.assertEqual(cohort.run_case('case-a', {}), first)
                self.assertEqual(send.call_count, 3)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 0)

    def test_http_worker_boundary_success_and_failures(self):
        from unittest.mock import Mock
        import live_https_worker as worker
        cases = self.fixtures()[:1]
        records = self.records(cases)['case-a']
        original_run = subprocess.run
        for mode in ('success', 'process_failure', 'malformed', 'partial', 'oversized'):
            with self.subTest(mode=mode):
                directory = self.work / mode
                directory.mkdir()
                with CampaignLedger(directory / 'ledger', create_policy=Policy(mode, 3, 3 * RESERVATION_NANODOLLARS, 0)) as ledger:
                    cohort = PairedCohort(OfflineCoordinator(ledger, directory), self.host, self.dll, cases,
                                          live_transport=CohortLiveTransport('SSLKEYLOGFILE'))
                    responses = iter(records.values())
                    posts = []
                    def fake_run(args, **kwargs):
                        if '-I' not in args:
                            return original_run(args, **kwargs)
                        self.assertEqual(kwargs['env']['COHORT_WORKER_KEY'], 'fake-secret')
                        self.assertNotIn('SSLKEYLOGFILE', kwargs['env'])
                        self.assertNotIn('SSL_CERT_FILE', kwargs['env'])
                        self.assertNotIn('SSL_CERT_DIR', kwargs['env'])
                        self.assertEqual(ledger.snapshot()['attempts'], len(posts) + 1)
                        if mode == 'process_failure':
                            posts.append('child-failure')
                            return subprocess.CompletedProcess(args, 2, b'')
                        body = next(responses) if mode == 'success' else (b'invalid' if mode == 'malformed' else b'{}')
                        headers = [('Content-Type', 'application/json')]
                        if mode == 'partial':
                            headers.append(('Content-Length', '4'))
                        if mode == 'oversized':
                            body = b'x' * (worker.MAX_BODY + 1)
                        response = Mock(status=200)
                        response.getheaders.return_value = headers
                        response.read.return_value = body
                        connection = Mock()
                        connection.getresponse.return_value = response
                        def post(*a, **kw):
                            posts.append(a)
                        connection.request.side_effect = post
                        with patch.dict(os.environ, kwargs['env'], clear=True), patch.object(worker.http.client, 'HTTPSConnection', return_value=connection) as constructor:
                            try:
                                raw = worker.exchange(kwargs['input'], kwargs['env']['COHORT_WORKER_KEY'])
                                result = subprocess.CompletedProcess(args, 0, raw)
                            except Exception:
                                result = subprocess.CompletedProcess(args, 2, b'')
                        self.assertEqual(constructor.call_args.args, ('api.typesafe.ai', 443))
                        connection.request.assert_called_once()
                        return result
                    with patch.dict(os.environ, {'SSLKEYLOGFILE': 'fake-secret', 'SSL_CERT_FILE': 'bad', 'SSL_CERT_DIR': 'bad'}), patch('cohort_live_transport.subprocess.run', side_effect=fake_run):
                        if mode == 'success':
                            first = cohort.run_case('case-a', {})
                            self.assertEqual(cohort.run_case('case-a', {}), first)
                            self.assertEqual(len(posts), 3)
                        else:
                            for _ in range(2):
                                with self.assertRaises(LedgerError):
                                    cohort.run_case('case-a', {})
                            self.assertEqual(len(posts), 1)
                            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)

    def test_huge_probability_is_unresolved_and_next_case_continues(self):
        cases = self.fixtures()
        records = self.records(cases)
        first = records['case-a'][('baseline', 'production', 0)]
        first = first.replace(b'"noul": 0.95', b'"noul": ' + b'9' * 400, 1)
        self.assertIn(b'9' * 400, first)
        responses = iter([first, *records['case-b'].values()])
        with CampaignLedger(self.ledger_path, create_policy=Policy('huge-number', 4, 4 * RESERVATION_NANODOLLARS, 0)) as ledger:
            cohort = PairedCohort(OfflineCoordinator(ledger, self.work), self.host, self.dll, cases,
                                  live_transport=CohortLiveTransport('TEST_COHORT_KEY'))
            with patch.object(CohortLiveTransport, 'exchange', autospec=True, side_effect=lambda *_: next(responses)) as send:
                result = cohort.run({})
                self.assertEqual(result['case-a'], {'status': 'unresolved'})
                self.assertEqual(result['case-b']['status'], 'complete')
                self.assertEqual(send.call_count, 4)
                self.assertEqual(cohort.run({})['case-a'], {'status': 'unresolved'})
                self.assertEqual(send.call_count, 4)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)
            self.assertEqual(len(list(self.work.glob('*.response'))), 4)

    def test_tls_environment_source_names_are_inert_in_child(self):
        for name in ('SSLKEYLOGFILE', 'SSL_CERT_FILE', 'SSL_CERT_DIR'):
            with self.subTest(name=name), patch.dict(os.environ, {name: 'fake-secret'}), patch('cohort_live_transport.subprocess.run', return_value=subprocess.CompletedProcess([], 0, b'{}')) as child:
                self.assertEqual(CohortLiveTransport(name).exchange(b'{}'), b'{}')
                env = child.call_args.kwargs['env']
                self.assertEqual(env['COHORT_WORKER_KEY'], 'fake-secret')
                for unsafe in ('SSLKEYLOGFILE', 'SSL_CERT_FILE', 'SSL_CERT_DIR'):
                    self.assertNotIn(unsafe, env)

    def test_deep_json_is_retained_without_resend_and_next_case_continues(self):
        cases = self.fixtures()
        records = self.records(cases)
        deep = b'[' * 2000 + b'0' + b']' * 2000
        responses = iter([deep, *records['case-b'].values()])
        with CampaignLedger(self.ledger_path, create_policy=Policy('deep-json', 4, 4 * RESERVATION_NANODOLLARS, 0)) as ledger:
            cohort = PairedCohort(OfflineCoordinator(ledger, self.work), self.host, self.dll, cases,
                                  live_transport=CohortLiveTransport('TEST_COHORT_KEY'))
            with patch.object(CohortLiveTransport, 'exchange', autospec=True, side_effect=lambda *_: next(responses)) as send:
                result = cohort.run({})
                self.assertEqual(result['case-a'], {'status': 'unresolved'})
                self.assertEqual(result['case-b']['status'], 'complete')
                self.assertEqual(send.call_count, 4)
                self.assertEqual(cohort.run({})['case-a'], {'status': 'unresolved'})
                self.assertEqual(send.call_count, 4)
            self.assertEqual(ledger.snapshot()['missing_outcomes'], 1)
            self.assertEqual(len(list(self.work.glob('*.response'))), 4)

    def test_live_runtime_identity_drift_stops_before_send(self):
        import sys
        from pathlib import Path
        cases = self.fixtures()[:1]
        with CampaignLedger(self.ledger_path, create_policy=Policy('live-drift', 3, 3 * RESERVATION_NANODOLLARS, 0)) as ledger:
            cohort = PairedCohort(OfflineCoordinator(ledger, self.work), self.host, self.dll, cases,
                                  live_transport=CohortLiveTransport('TEST_COHORT_KEY'))
            original_read = Path.read_bytes
            def changed_read(path):
                data = original_read(path)
                return data + b'changed' if path.name == 'live_stage_campaign.py' else data
            with patch.object(CohortLiveTransport, 'exchange', autospec=True) as send:
                with patch.object(Path, 'read_bytes', changed_read):
                    with self.assertRaises(LedgerError):
                        cohort.run_case('case-a', {})
                with patch.object(sys, 'executable', self.host):
                    with self.assertRaises(LedgerError):
                        cohort.run_case('case-a', {})
                send.assert_not_called()
            self.assertEqual(ledger.snapshot()['attempts'], 0)
