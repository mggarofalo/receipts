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
MAX_LEDGER_BYTES = 1_048_576


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
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode("ascii")


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
            for line in data.splitlines():
                row = json.loads(line, object_pairs_hook=_unique)
                if set(row) != {"sequence", "previous", "body", "hash"}:
                    raise LedgerError("invalid record fields")
                unsigned = {key: row[key] for key in ("sequence", "previous", "body")}
                if (type(row["sequence"]) is not int or row["sequence"] != len(records)
                        or row["previous"] != (records[-1]["hash"] if records else "0" * 64)
                        or row["hash"] != hashlib.sha256(_canonical(unsigned)).hexdigest()):
                    raise LedgerError("journal integrity check failed")
                body = row["body"]
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
