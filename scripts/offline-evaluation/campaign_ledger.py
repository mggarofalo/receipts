"""Offline reservation journal. No transport, credentials, payload storage or retries."""

from contextlib import contextmanager
import ctypes
from dataclasses import asdict, dataclass
import hashlib
import json
import os
from pathlib import Path
import stat
import threading
import uuid


TOKENS_PER_ATTEMPT = 65_536
# USD 0.042 / million input tokens = 42 nanodollars / input token.
NANODOLLARS_PER_TOKEN = 42
RESERVATION_NANODOLLARS = TOKENS_PER_ATTEMPT * NANODOLLARS_PER_TOKEN
MAX_LEDGER_BYTES = 2_097_152
# Complete canonical record bytes, including chain metadata and terminating LF.
RECORD_BYTE_CAPS = {"policy": 2_048, "bind": 1_179_648, "verifier": 524_288, "verifiers": 524_288,
                    "start": 512, "attempt": 512, "outcome": 512, "finish": 512}


def required_capacity(policy):
    """One binding, one legacy verifier OR entire batched vector, four records/call.

    All five batch payloads share the existing verifier record cap. No journal
    expansion: 96 calls still require at most 1,902,592 canonical bytes.
    """
    return (RECORD_BYTE_CAPS["policy"] + RECORD_BYTE_CAPS["bind"]
            + RECORD_BYTE_CAPS["verifier"] + policy.max_calls * 4 * 512)


def _record_kind(body):
    if not isinstance(body, dict):
        raise LedgerError("record body object required")
    kind = body.get("kind")
    if kind == "stage":
        if set(body) != {"kind", "event"} or not isinstance(body["event"], dict):
            raise LedgerError("invalid stage event")
        event = body["event"]
        kind = event.get("op")
        fields = {
            "start": {"op", "index", "request_sha256"},
            "finish": {"op", "index", "receipt_sha256"},
            "verifier": {"op", "payload", "plan_sha256", "responses"},
            "verifiers": {"op", "batches", "plan_sha256", "responses"},
        }
        if kind == "bind":
            required = {"op", "format", "plan", "artifacts", "private_root"}
            if (not required <= set(event) or set(event) - required - {"executor", "transport"}
                    or event["format"] not in ("offline-stage-v1", "offline-paired-stage-v1", "offline-batched-paired-stage-v1", "live-paired-stage-v1")
                    or not isinstance(event["plan"], dict) or not isinstance(event["artifacts"], dict)
                    or not isinstance(event["private_root"], str)):
                raise LedgerError("invalid binding event")
            plan = event["plan"]
            if (set(plan) != {"case", "plan_sha256", "production", "shortlist"}
                    or not isinstance(plan["case"], str) or not 0 < len(plan["case"]) <= 100
                    or not _sha(plan["plan_sha256"]) or not isinstance(plan["production"], str)
                    or not isinstance(plan["shortlist"], list) or not 1 <= len(plan["shortlist"]) <= 94
                    or any(not isinstance(value, str) for value in plan["shortlist"])
                    or any(key in event and not isinstance(event[key], dict) for key in ("executor", "transport"))):
                raise LedgerError("invalid bound plan")
        elif kind in fields:
            if set(event) != fields[kind]:
                raise LedgerError("invalid stage fields")
            if kind in ("start", "finish"):
                key = "request_sha256" if kind == "start" else "receipt_sha256"
                if type(event["index"]) is not int or not 0 <= event["index"] < 96 or not _sha(event[key]):
                    raise LedgerError("invalid stage index/hash")
            elif kind == "verifiers":
                batches = event["batches"]
                if (not isinstance(batches, list) or len(batches) > 5
                        or not isinstance(event["responses"], list) or len(event["responses"]) > 95
                        or any(not _sha(value) for value in event["responses"])
                        or (not batches and event["plan_sha256"] is not None)
                        or (batches and not _sha(event["plan_sha256"]))):
                    raise LedgerError("invalid batched verifier binding")
                for index, batch in enumerate(batches):
                    if (not isinstance(batch, dict)
                            or set(batch) != {"index", "isGlobalCoverage", "payload", "questionIds"}
                            or type(batch["index"]) is not int or batch["index"] != index
                            or type(batch["isGlobalCoverage"]) is not bool
                            or batch["isGlobalCoverage"] != (index == len(batches) - 1)
                            or not isinstance(batch["payload"], str) or not batch["payload"]
                            or len(batch["payload"].encode("utf-8")) > 32 * 1024
                            or not isinstance(batch["questionIds"], list) or not batch["questionIds"]
                            or any(not isinstance(q, str) or not q for q in batch["questionIds"])
                            or len(set(batch["questionIds"])) != len(batch["questionIds"])):
                        raise LedgerError("invalid ordered verification batch")
                if batches and len(batches) < 2:
                    raise LedgerError("verification paths and final global coverage required")
            elif (not isinstance(event["responses"], list) or len(event["responses"]) > 95
                  or any(not _sha(value) for value in event["responses"])
                  or (event["payload"] is None and event["plan_sha256"] is not None)
                  or (event["payload"] is not None and
                      (not isinstance(event["payload"], str) or not event["payload"] or not _sha(event["plan_sha256"])))):
                raise LedgerError("invalid verifier event")
        else:
            raise LedgerError("unknown stage kind")
    elif kind == "policy":
        if (set(body) != {"kind", "version", "policy"} or type(body["version"]) is not int
                or body["version"] != 1 or not isinstance(body["policy"], dict)
                or set(body["policy"]) != set(Policy.__dataclass_fields__)):
            raise LedgerError("invalid policy header")
        Policy(**body["policy"]).validate()
    elif kind == "attempt":
        if (set(body) != {"kind", "id", "payload_sha256", "payload_bytes"}
                or not isinstance(body["id"], str) or len(body["id"]) != 36
                or not _sha(body["payload_sha256"]) or type(body["payload_bytes"]) is not int):
            raise LedgerError("invalid attempt fields")
    elif kind == "outcome":
        if (set(body) != {"kind", "id", "status"} or not isinstance(body["id"], str)
                or body["status"] not in ("success", "error", "unknown")):
            raise LedgerError("invalid outcome fields")
    else:
        raise LedgerError("unknown record kind")
    return kind


def _sha(value):
    return isinstance(value, str) and len(value) == 64 and all(c in "0123456789abcdef" for c in value)


def _check_record(body, data):
    kind = _record_kind(body)
    if len(data) > RECORD_BYTE_CAPS[kind]:
        raise LedgerError("record size limit reached")
    return kind


def _check_counts(records, policy):
    counts = {}
    for row in records:
        kind = _record_kind(row["body"])
        if kind == "verifiers":
            kind = "verifier"
        counts[kind] = counts.get(kind, 0) + 1
        cap = 1 if kind in ("policy", "bind", "verifier") else policy.max_calls
        if counts[kind] > cap:
            raise LedgerError("record count exceeds campaign policy")
    return counts


def _check_capacity(policy):
    if required_capacity(policy) > MAX_LEDGER_BYTES:
        raise LedgerError("insufficient journal capacity for campaign policy")


class LedgerError(ValueError):
    """Invalid journal or policy; never dispatch after this error."""


class LedgerBusy(LedgerError):
    """Another owner holds the journal; no automatic retry."""


def require_plain_absolute_path(path, *, allow_missing_leaf=False):
    """Reject link/reparse ancestry before following any component.

    The caller must keep its private directory and ancestry stable while operating;
    this check is not protection against malicious concurrent filesystem changes.
    """
    path = Path(path)
    if not path.is_absolute() or '..' in path.parts:
        raise LedgerError("absolute path without parent traversal required")
    current = Path(path.anchor)
    for part in ('', *path.parts[1:]):
        if part:
            current = current / part
        try:
            info = current.lstat()
        except FileNotFoundError:
            if allow_missing_leaf and current == path:
                return path
            raise FileNotFoundError("path ancestor does not exist") from None
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise LedgerError("reparse/link ancestry is not permitted")
    return path


@dataclass(frozen=True)
class Policy:
    campaign_id: str
    max_calls: int
    budget_nanodollars: int
    unknown_carry_nanodollars: int
    # Transport-size safeguard only. This is NOT a token estimate or token cap.
    max_payload_bytes: int = 65_536
    model: str = "jev-1.14.0"
    input_nanodollars_per_token: int = NANODOLLARS_PER_TOKEN
    reserved_tokens_per_attempt: int = TOKENS_PER_ATTEMPT

    def validate(self):
        if not isinstance(self.campaign_id, str) or not 1 <= len(self.campaign_id) <= 100:
            raise LedgerError("campaign_id required (1..100 characters)")
        for value, minimum, maximum in (
            (self.max_calls, 1, 96),
            (self.budget_nanodollars, 1, 5_000_000_000),
            (self.unknown_carry_nanodollars, 0, self.budget_nanodollars),
            (self.max_payload_bytes, 1, 262_144),
        ):
            if type(value) is not int or not minimum <= value <= maximum:
                raise LedgerError("policy integer outside permitted range")
        if (self.model != "jev-1.14.0"
                or type(self.input_nanodollars_per_token) is not int
                or self.input_nanodollars_per_token != NANODOLLARS_PER_TOKEN
                or type(self.reserved_tokens_per_attempt) is not int
                or self.reserved_tokens_per_attempt != TOKENS_PER_ATTEMPT):
            raise LedgerError("model/pricing assumptions differ from this ledger version")


def _canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True, allow_nan=False).encode("ascii")


def _unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise LedgerError("duplicate JSON key")
        result[key] = value
    return result


class CampaignLedger:
    """Single nonblocking cooperative owner, supported on Windows local fixed disks.

    Create once, then reopen the SAME journal for every worker/restart. Instances
    are context managers. The lock is held until close, including between writes.
    """

    def __init__(self, path, *, create_policy=None):
        self.path = require_plain_absolute_path(path, allow_missing_leaf=create_policy is not None)
        require_plain_absolute_path(str(self.path) + ".lock", allow_missing_leaf=True)
        # Stage events may contain exact private payloads. Reject checkout
        # ancestry before creating either the journal or its persistent lock.
        for candidate in (self.path, Path(str(self.path) + ".lock")):
            if any((parent / ".git").exists() for parent in (candidate.parent, *candidate.parents)):
                raise LedgerError("private journal and lock outside a checkout required")
        self._lock_file = None
        self._journal = None
        self._mutex = threading.Lock()
        self._poisoned = False
        if create_policy is not None:
            create_policy.validate()
        if os.name != "nt":
            raise LedgerError("only Windows local fixed disks are supported")
        if ctypes.windll.kernel32.GetDriveTypeW(str(self.path.anchor)) != 3:
            raise LedgerError("journal must be on a local fixed disk")
        import msvcrt
        self._msvcrt = msvcrt
        try:
            # Never remove/replace this sidecar: owners must lock the same file.
            self._lock_file = open(str(self.path) + ".lock", "a+b", buffering=0)
            if os.fstat(self._lock_file.fileno()).st_size == 0:
                self._lock_file.write(b"\0")
            self._lock_file.seek(0)
            try:
                msvcrt.locking(self._lock_file.fileno(), msvcrt.LK_NBLCK, 1)
            except OSError as error:
                raise LedgerBusy("journal already owned; no retry") from error
            self._journal = open(self.path, "x+b" if create_policy else "r+b", buffering=0)
            if create_policy:
                self._append({"kind": "policy", "version": 1, "policy": asdict(create_policy)}, [])
            self._read()
        except BaseException:
            self.close()
            raise

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()

    def close(self):
        if self._journal is not None:
            self._journal.close()
            self._journal = None
        if self._lock_file is not None:
            # Closing also releases locks when acquisition/initialization failed.
            self._lock_file.close()
            self._lock_file = None

    @contextmanager
    def _operation(self):
        if not self._mutex.acquire(blocking=False):
            raise LedgerBusy("another thread owns journal operation; no retry")
        try:
            if self._journal is None or self._poisoned:
                raise LedgerError("closed or uncertain journal; reopen and inspect")
            yield
        finally:
            self._mutex.release()

    def _append(self, body, records):
        previous = records[-1]["hash"] if records else "0" * 64
        record = {"sequence": len(records), "previous": previous, "body": body}
        record["hash"] = hashlib.sha256(_canonical(record)).hexdigest()
        data = _canonical(record) + b"\n"
        kind = _check_record(body, data)
        policy = Policy(**(records[0]["body"]["policy"] if records else body["policy"]))
        _check_counts([*records, record], policy)
        if kind in ("start", "attempt"):
            _check_capacity(policy)
        self._journal.seek(0, os.SEEK_END)
        if self._journal.tell() + len(data) > MAX_LEDGER_BYTES:
            raise LedgerError("journal size limit reached")
        # Poison on ANY uncertain write, including a successful write followed by
        # failed fsync. Caller never receives permission in that case.
        self._poisoned = True
        if self._journal.write(data) != len(data):
            raise LedgerError("partial journal write; journal retained for inspection")
        os.fsync(self._journal.fileno())
        self._poisoned = False

    def _read(self):
        self._journal.seek(0)
        data = self._journal.read(MAX_LEDGER_BYTES + 1)
        if not data or len(data) > MAX_LEDGER_BYTES or not data.endswith(b"\n"):
            raise LedgerError("empty, oversized or incomplete journal")
        records = []
        attempts = {}
        outcomes = set()
        policy = None
        try:
            for line in data[:-1].split(b"\n"):
                row = json.loads(line, object_pairs_hook=_unique)
                if not isinstance(row, dict) or set(row) != {"sequence", "previous", "body", "hash"}:
                    raise LedgerError("invalid record fields")
                unsigned = {key: row[key] for key in ("sequence", "previous", "body")}
                if (type(row["sequence"]) is not int or row["sequence"] != len(records)
                        or row["previous"] != (records[-1]["hash"] if records else "0" * 64)
                        or row["hash"] != hashlib.sha256(_canonical(unsigned)).hexdigest()):
                    raise LedgerError("journal integrity check failed")
                if line + b"\n" != _canonical(row) + b"\n":
                    raise LedgerError("noncanonical journal record")
                body = row["body"]
                _check_record(body, line + b"\n")
                if not records:
                    if set(body) != {"kind", "version", "policy"} or body["kind"] != "policy" or type(body["version"]) is not int or body["version"] != 1:
                        raise LedgerError("invalid policy header")
                    if set(body["policy"]) != set(Policy.__dataclass_fields__):
                        raise LedgerError("invalid policy fields")
                    policy = Policy(**body["policy"])
                    policy.validate()
                elif body["kind"] == "attempt":
                    if set(body) != {"kind", "id", "payload_sha256", "payload_bytes"}:
                        raise LedgerError("invalid attempt fields")
                    if (str(uuid.UUID(body["id"])) != body["id"] or body["id"] in attempts
                            or type(body["payload_bytes"]) is not int
                            or not 0 < body["payload_bytes"] <= policy.max_payload_bytes
                            or len(body["payload_sha256"]) != 64
                            or any(c not in "0123456789abcdef" for c in body["payload_sha256"])):
                        raise LedgerError("invalid attempt")
                    attempts[body["id"]] = body
                elif body["kind"] == "stage":
                    if set(body) != {"kind", "event"} or not isinstance(body["event"], dict):
                        raise LedgerError("invalid stage event")
                elif body["kind"] == "outcome":
                    if (set(body) != {"kind", "id", "status"} or body["id"] not in attempts
                            or body["id"] in outcomes or body["status"] not in ("success", "error", "unknown")):
                        raise LedgerError("invalid outcome")
                    outcomes.add(body["id"])
                else:
                    raise LedgerError("unknown record kind")
                records.append(row)
        except (KeyError, TypeError, ValueError, AttributeError, OverflowError) as error:
            raise LedgerError("malformed journal; no automatic repair") from error
        _check_counts(records, policy)
        reserved = policy.unknown_carry_nanodollars + len(attempts) * RESERVATION_NANODOLLARS
        if len(attempts) > policy.max_calls or reserved > policy.budget_nanodollars:
            raise LedgerError("journal exceeds campaign policy")
        return records, policy, attempts, outcomes, reserved

    def snapshot(self):
        with self._operation():
            _, policy, attempts, outcomes, reserved = self._read()
            return {"policy": asdict(policy), "attempts": len(attempts),
                    "missing_outcomes": len(attempts) - len(outcomes),
                    "reserved_nanodollars": reserved}

    def reserve_attempt(self, payload):
        """Durably count an attempt BEFORE dispatch; returns a single-use audit ID.

        No dispatch exists here. The caller must never reuse an ID or retry a
        request. Uncertain persistence raises without returning an ID.
        """
        with self._operation():
            records, policy, attempts, _, reserved = self._read()
            if type(payload) is not bytes or not 0 < len(payload) <= policy.max_payload_bytes:
                raise LedgerError("payload must be nonempty bytes within the byte cap")
            if len(attempts) >= policy.max_calls or reserved + RESERVATION_NANODOLLARS > policy.budget_nanodollars:
                raise LedgerError("campaign call/spend allowance exhausted")
            attempt_id = str(uuid.uuid4())
            self._append({"kind": "attempt", "id": attempt_id,
                          "payload_sha256": hashlib.sha256(payload).hexdigest(),
                          "payload_bytes": len(payload)}, records)
            return attempt_id

    def record_outcome(self, attempt_id, status):
        """Audit only: every outcome retains the FULL reservation, usage unknown.

        No usage release/reconciliation interface is provided.
        """
        with self._operation():
            records, _, attempts, outcomes, _ = self._read()
            if attempt_id not in attempts or attempt_id in outcomes or status not in ("success", "error", "unknown"):
                raise LedgerError("invalid or duplicate outcome")
            self._append({"kind": "outcome", "id": attempt_id, "status": status}, records)

    def stage_history(self):
        """Read immutable stage evidence under the same lifetime campaign lock."""
        with self._operation():
            records, _, _, _, _ = self._read()
            return [row["body"] for row in records[1:]]

    def append_stage_event(self, event):
        """Durable offline controller evidence; never releases a reservation."""
        with self._operation():
            records, _, _, _, _ = self._read()
            if not isinstance(event, dict):
                raise LedgerError("stage event object required")
            self._append({"kind": "stage", "event": event}, records)
