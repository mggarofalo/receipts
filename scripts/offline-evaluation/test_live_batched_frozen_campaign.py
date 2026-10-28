"""Public live batched API, frozen Python/C#, invented data and fake send only."""
import json
import os
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

import test_live_frozen_campaign as live_fixture


def frozen_case(config_path, scenario):
    from campaign_ledger import CampaignLedger, LedgerError, Policy, RESERVATION_NANODOLLARS
    from frozen_runner import FrozenRunner
    from live_batched_stage_campaign import LiveBatchedPairedStageCampaign
    import live_stage_campaign
    cfg = json.loads(Path(config_path).read_text())
    root = Path(config_path).parent
    assert sys.flags.isolated and sys.flags.dont_write_bytecode
    assert sys.executable == str(Path(cfg['roots']['dependency']) / 'python/python.exe')
    assert all(any(Path(p).is_relative_to(Path(cfg['roots'][kind]))
                   for kind in ('source', 'dependency')) for p in sys.path)
    runner = FrozenRunner(cfg['roots'], cfg['artifacts'], host=cfg['host'], assembly=cfg['assembly'])
    input_path = root / 'input.json'
    input_path.write_text(json.dumps({'purchase': {'description': 'Invented fruit purchase',
        'store': None, 'productCode': None}, 'taxonomyRevision': 'synthetic-v1',
        'taxonomy': [{'id': f'item-{i}', 'name': f'Fruit {i}',
                      'definition': f'Invented fruit {i}', 'parentId': None} for i in range(33)]}))
    def controller(ledger):
        return LiveBatchedPairedStageCampaign(ledger, runner, input_path, case='synthetic-batched',
            private_root=root, key_environment='NEVER_READ_SYNTHETIC_KEY',
            python_executable=sys.executable,
            worker_path=str(Path(live_stage_campaign.__file__).with_name('live_https_worker.py')))
    response = live_fixture.paired_tests.ActualPairedStageTests.response
    def send(payload):
        # Assert the durable unknown reservation exists at the sole mocked boundary.
        assert ledger.snapshot()['missing_outcomes'] == 1
        raw = response(None, payload)
        if scenario == 'unknown-usage':
            reply = json.loads(raw)
            reply['usage']['input_tokens'] = None
            return json.dumps(reply).encode()
        if scenario == 'malformed-global' and json.loads(payload)['questions'].keys() == global_ids:
            # Valid generic envelope, invalid real C# JSON parser (same negative control as legacy).
            return b'\xef\xbb\xbf' + raw
        return raw
    global_ids = set()
    with CampaignLedger(root / 'ledger', create_policy=Policy('synthetic-live-batched', 96, 5_000_000_000, 0)) as ledger:
        campaign = controller(ledger)
        with patch.object(LiveBatchedPairedStageCampaign, '_send_reserved', side_effect=send) as transport:
            if scenario == 'unknown-usage':
                try:
                    campaign.exchange('baseline')
                except LedgerError:
                    pass
                else:
                    raise AssertionError('unknown usage accepted')
            else:
                for name in campaign.names:
                    campaign.exchange(name)
                before = transport.call_count
                if scenario == 'overflow':
                    # Exhaust the aggregate byte cap with an otherwise actual-derived vector.
                    export = campaign._export_verifiers
                    def oversized(done):
                        event = export(done)
                        event['batches'][0]['questionIds'][0] += 'x' * 524288
                        return event
                    with patch.object(campaign, '_export_verifiers', side_effect=oversized):
                        try:
                            campaign.bind_verifier()
                        except LedgerError:
                            pass
                        else:
                            raise AssertionError('oversized aggregate accepted')
                    assert transport.call_count == before
                    assert ledger.snapshot()['missing_outcomes'] == 0
                    assert not any(r.get('event', {}).get('op') == 'verifiers' for r in ledger.stage_history())
                else:
                    campaign.bind_verifier()
                    batches = ledger.stage_history()[-1]['event']['batches']
                    assert 3 <= len(batches) <= 5 and batches[-1]['isGlobalCoverage']
                    global_ids = set(json.loads(batches[-1]['payload'])['questions'])
                    for i, batch in enumerate(batches):
                        name = 'global-coverage' if batch['isGlobalCoverage'] else f'verifier:{i}'
                        try:
                            campaign.exchange(name)
                        except LedgerError:
                            assert scenario == 'malformed-global' and batch['isGlobalCoverage']
                        else:
                            assert scenario != 'malformed-global' or not batch['isGlobalCoverage']
            failed = scenario in ('unknown-usage', 'malformed-global')
            assert ledger.snapshot()['missing_outcomes'] == int(failed)
            attempts = ledger.snapshot()['attempts']
            assert ledger.snapshot()['reserved_nanodollars'] == attempts * RESERVATION_NANODOLLARS
    with CampaignLedger(root / 'ledger') as ledger:
        campaign = controller(ledger)
        with patch.object(LiveBatchedPairedStageCampaign, '_send_reserved') as transport:
            if failed:
                try:
                    campaign.exchange('baseline' if scenario == 'unknown-usage' else 'global-coverage')
                except LedgerError:
                    pass
                else:
                    raise AssertionError('failed campaign resumed')
                assert not campaign.complete
            elif scenario == 'success':
                assert campaign.complete and campaign.result['candidate']['failure'] is None
            transport.assert_not_called()
    print(json.dumps({'actual_csharp': True, 'network_calls': 0,
                      'python_inventory_files': len(cfg['artifacts']['dependency'])}))


@unittest.skipUnless(os.name == 'nt', 'Windows frozen runtimes')
class FrozenLiveBatchedCampaignTests(unittest.TestCase):
    FROZEN_CASE_SCRIPT = Path(__file__).name
    setUpClass = classmethod(live_fixture.FrozenLiveCampaignTests.setUpClass.__func__)
    run_case = live_fixture.FrozenLiveCampaignTests.run_case

    def test_multi_batch_complete_and_public_reopen(self):
        self.run_case('success')

    def test_malformed_global_is_terminal_after_reopen(self):
        self.run_case('malformed-global')

    def test_unknown_usage_is_terminal_after_reopen(self):
        self.run_case('unknown-usage')

    def test_aggregate_overflow_sends_no_verification(self):
        self.run_case('overflow')


if __name__ == '__main__':
    if len(sys.argv) == 4 and sys.argv[1] == '--frozen-case':
        frozen_case(sys.argv[2], sys.argv[3])
    else:
        unittest.main()
