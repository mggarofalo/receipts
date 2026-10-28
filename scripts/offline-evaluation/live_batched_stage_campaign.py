"""Explicit opt-in live batched controller; no CLI or production registration.

Transport and durable reservation ordering are shared with the legacy live seam.
Actual frozen C# prepares, validates and aggregates the complete ordered vector.
"""
from pathlib import Path

import batched_stage_campaign
from batched_stage_campaign import BatchedPairedStageCampaign
from campaign_ledger import LedgerError
from live_stage_campaign import LivePairedStageCampaign


class LiveBatchedPairedStageCampaign(LivePairedStageCampaign, BatchedPairedStageCampaign):
    FORMAT = 'live-batched-paired-stage-v1'

    def _verify_live_files(self, artifacts):
        # Both additional executing modules belong to the same frozen inventory.
        if any(str(Path(p)) not in artifacts['source'] for p in
               (__file__, batched_stage_campaign.__file__)):
            raise LedgerError('executing batched Python source inventories required')
        super()._verify_live_files(artifacts)
