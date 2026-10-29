"""Synthetic journal capacity evidence; no credentials, network or private data."""
import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from campaign_ledger import (CampaignLedger, LedgerError, Policy, RECORD_BYTE_CAPS,
                             RESERVATION_NANODOLLARS, _canonical, required_capacity)
from guarded_exchange import RecordedTransport
from stage_campaign import StageCampaign
from test_stage_campaign import PAYLOAD, REPLY


def encode(bodies):
    records = []
    for body in bodies:
        row = {"sequence": len(records), "previous": records[-1]["hash"] if records else "0" * 64,
               "body": body}
        row["hash"] = hashlib.sha256(_canonical(row)).hexdigest()
        records.append(row)
    return b"".join(_canonical(row) + b"\n" for row in records)


@unittest.skipUnless(os.name == "nt", "Windows ledger")
class BoundedCapacityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.path = self.root / "ledger"
        artifact = self.root / "frozen"
        artifact.write_bytes(b"synthetic")
        group = {str(artifact): hashlib.sha256(b"synthetic").hexdigest()}
        self.artifacts = {"source": group, "executable": group}

    def policy(self, calls=96, budget=5_000_000_000):
        return Policy("synthetic", calls, budget, 0, max_payload_bytes=262_144)

    def binding(self):
        return {"op": "bind", "format": "offline-stage-v1", "plan": {"case": "synthetic", "plan_sha256": "a" * 64, "production": "", "shortlist": ["synthetic"]},
                "artifacts": self.artifacts, "private_root": str(self.root)}

    def bodies(self):
        return [json.loads(line)["body"] for line in self.path.read_bytes().splitlines()]

    def test_real_sized_binding_and_22_stages_survive_every_restart(self):
        payload = json.loads(PAYLOAD)
        payload["state"] = "x" * 49_000
        text = json.dumps(payload, separators=(",", ":"))
        plan = {"case": "synthetic-22-stages", "plan_sha256": "a" * 64,
                "production": text, "shortlist": [text] * 20}
        with CampaignLedger(self.path, create_policy=self.policy(22)) as ledger:
            # Compute the full chain record size before binding; metadata lengths are fixed.
            binding = {"op": "bind", "format": StageCampaign.FORMAT, "plan": plan,
                       "artifacts": self.artifacts, "private_root": str(self.root)}
            policy_body = self.bodies()[0]
            size = len(encode([policy_body, {"kind": "stage", "event": binding}])) - self.path.stat().st_size
            extra = 1_051_284 - size
            self.assertGreater(extra, 0)
            plan["production"] = text.replace("x" * 49_000, "x" * (49_000 + extra))
            StageCampaign(ledger, plan, artifacts=self.artifacts, private_root=self.root)
            self.assertEqual(len(self.path.read_bytes().splitlines(keepends=True)[1]), 1_051_284)
        names = ["baseline", *[f"shortlist:{i}" for i in range(20)], "verifier"]
        for name in names:
            with CampaignLedger(self.path) as ledger:
                controller = StageCampaign(ledger, plan, artifacts=self.artifacts, private_root=self.root)
                if name == "verifier":
                    controller.bind_verifier(PAYLOAD, "b" * 64)
                controller.exchange(name, RecordedTransport(REPLY))
        with CampaignLedger(self.path) as ledger:
            self.assertEqual(ledger.snapshot()["attempts"], 22)
            self.assertEqual(ledger.snapshot()["reserved_nanodollars"], 22 * RESERVATION_NANODOLLARS)
            self.assertTrue(StageCampaign(ledger, plan, artifacts=self.artifacts, private_root=self.root).complete)

    def test_bind_and_verifier_full_record_caps_append_and_replay(self):
        for op in ("bind", "verifier"):
            for excess in (0, 1):
                with self.subTest(op=op, excess=excess):
                    self.path.unlink(missing_ok=True)
                    with CampaignLedger(self.path, create_policy=self.policy()) as ledger:
                        event = self.binding() if op == "bind" else {"op": "verifier", "payload": "x",
                                  "plan_sha256": "a" * 64, "responses": []}
                        body = {"kind": "stage", "event": event}
                        header = self.bodies()[0]
                        size = len(encode([header, body])) - self.path.stat().st_size
                        padding = RECORD_BYTE_CAPS[op] - size + excess
                        if op == "bind":
                            event["plan"]["production"] = "x" * padding
                        else:
                            event["payload"] += "x" * padding
                        if excess:
                            before = self.path.read_bytes()
                            with self.assertRaisesRegex(LedgerError, "record size"):
                                ledger.append_stage_event(event)
                            self.assertEqual(self.path.read_bytes(), before)
                        else:
                            ledger.append_stage_event(event)
                    if excess:
                        self.path.write_bytes(encode([header, body]))
                        with self.assertRaises(LedgerError):
                            CampaignLedger(self.path)
                    else:
                        with CampaignLedger(self.path) as ledger:
                            self.assertEqual(len(ledger.stage_history()), 1)

    def test_cardinality_and_unknown_malformed_events_fail_closed(self):
        with CampaignLedger(self.path, create_policy=self.policy(1)) as ledger:
            ledger.append_stage_event(self.binding())
            before = self.path.read_bytes()
            for event in (self.binding(), {"op": "unexpected"}, {"op": "start", "index": True,
                           "request_sha256": "a" * 64}):
                with self.assertRaises(LedgerError):
                    ledger.append_stage_event(event)
                self.assertEqual(self.path.read_bytes(), before)
        valid = self.bodies()
        bad = [
            [*valid, valid[-1]],
            [*valid, {"kind": "stage", "event": {"op": "unexpected"}}],
            [*valid, {"kind": "stage", "event": []}],
            [*valid, {"kind": "unknown"}],
            [*valid, {"kind": "stage", "event": {"op": "finish", "index": -1, "receipt_sha256": "a" * 64}}],
        ]
        for bodies in bad:
            damaged = encode(bodies)
            self.path.write_bytes(damaged)
            with self.assertRaises(LedgerError):
                CampaignLedger(self.path)
            self.assertEqual(self.path.read_bytes(), damaged)
        self.path.write_bytes(encode(valid).replace(b'"sequence":0', b'"sequence": 0'))
        with self.assertRaises(LedgerError):
            CampaignLedger(self.path)

    def test_capacity_preflight_before_start_and_reservation(self):
        self.assertEqual(required_capacity(self.policy()), 1_902_592)
        with CampaignLedger(self.path, create_policy=self.policy()) as ledger:
            ledger.append_stage_event(self.binding())
            before = self.path.read_bytes()
            with patch("campaign_ledger.MAX_LEDGER_BYTES", 1_902_591):
                with self.assertRaisesRegex(LedgerError, "insufficient journal capacity"):
                    ledger.append_stage_event({"op": "start", "index": 0, "request_sha256": "a" * 64})
                with self.assertRaisesRegex(LedgerError, "insufficient journal capacity"):
                    ledger.reserve_attempt(PAYLOAD)
            self.assertEqual(self.path.read_bytes(), before)
            self.assertEqual(ledger.snapshot()["attempts"], 0)

    def test_call_and_budget_exhaustion_remain_charged_after_restart(self):
        with CampaignLedger(self.path, create_policy=self.policy(96, RESERVATION_NANODOLLARS)) as ledger:
            ledger.append_stage_event(self.binding())
            ledger.reserve_attempt(PAYLOAD)
        with CampaignLedger(self.path) as ledger:
            with self.assertRaisesRegex(LedgerError, "allowance exhausted"):
                ledger.reserve_attempt(PAYLOAD)
            self.assertEqual(ledger.snapshot()["missing_outcomes"], 1)

    def test_exact_capacity_and_96_attempt_limit_survive_restart(self):
        with CampaignLedger(self.path, create_policy=self.policy()) as ledger:
            with patch("campaign_ledger.MAX_LEDGER_BYTES", 1_902_592):
                for _ in range(96):
                    ledger.record_outcome(ledger.reserve_attempt(PAYLOAD), "success")
                before = self.path.read_bytes()
                with self.assertRaisesRegex(LedgerError, "allowance exhausted"):
                    ledger.reserve_attempt(PAYLOAD)
                self.assertEqual(self.path.read_bytes(), before)
        with CampaignLedger(self.path) as ledger:
            self.assertEqual(ledger.snapshot()["attempts"], 96)
            self.assertEqual(ledger.snapshot()["reserved_nanodollars"], 96 * RESERVATION_NANODOLLARS)

    def test_stage_record_counts_rejected_on_append_and_replay(self):
        for event in ({"op": "start", "index": 0, "request_sha256": "a" * 64},
                      {"op": "finish", "index": 0, "receipt_sha256": "a" * 64},
                      {"op": "verifier", "payload": None, "plan_sha256": None, "responses": []}):
            with self.subTest(op=event["op"]):
                self.path.unlink(missing_ok=True)
                with CampaignLedger(self.path, create_policy=self.policy(1)) as ledger:
                    ledger.append_stage_event(event)
                    before = self.path.read_bytes()
                    with self.assertRaisesRegex(LedgerError, "record count"):
                        ledger.append_stage_event(event)
                    self.assertEqual(self.path.read_bytes(), before)
                bodies = self.bodies()
                self.path.write_bytes(encode([*bodies, bodies[-1]]))
                with self.assertRaises(LedgerError):
                    CampaignLedger(self.path)

    def test_short_binding_write_poisons_instance_and_torn_replay(self):
        with CampaignLedger(self.path, create_policy=self.policy()) as ledger:
            stream = ledger._journal
            class ShortWriter:
                def __getattr__(self, key):
                    return getattr(stream, key)
                def write(self, data):
                    return stream.write(data[:len(data)//2])
            ledger._journal = ShortWriter()
            with self.assertRaisesRegex(LedgerError, "partial journal write"):
                ledger.append_stage_event(self.binding())
            with self.assertRaisesRegex(LedgerError, "uncertain journal"):
                ledger.reserve_attempt(PAYLOAD)
        with self.assertRaises(LedgerError):
            CampaignLedger(self.path)


if __name__ == "__main__":
    unittest.main()
