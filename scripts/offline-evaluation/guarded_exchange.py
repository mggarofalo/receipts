"""Offline-only reservation/response seam. No HTTP, environment or credential access.

Only RecordedTransport is accepted. This is not authorization or a live runner.
"""
import hashlib
import json
import math
import os
from pathlib import Path
import threading

from campaign_ledger import CampaignLedger, LedgerBusy, LedgerError, _unique, require_plain_absolute_path


MODEL = "jev-1.14.0"
MAX_RESPONSE_BYTES = 1_048_576
# A stricter process-wide owner also keeps different campaigns sequential. The
# ledger itself holds its nonblocking OS lock for the entire instance lifetime.
_EXCHANGE_OWNER = threading.Lock()


class RecordedTransport:
    """One supplied response, consumed once; cannot delegate to a network client."""

    def __init__(self, response):
        if type(response) is not bytes:
            raise LedgerError("recorded response bytes required")
        self.response = response
        self.used = False

    def exchange(self, payload):
        if self.used:
            raise LedgerError("recorded response already consumed")
        self.used = True
        return self.response


def verify_artifacts(artifacts):
    """Verify explicit source AND executable artifact paths against reviewed hashes.

    Hashes identify the files inspected, not a trusted build or race-proof attestation.
    Caller must freeze the build and supply its complete dependency/source closure.
    """
    if not artifacts or set(artifacts) not in ({"source", "executable"}, {"source", "executable", "dependency"}):
        raise LedgerError("source and executable artifacts required")
    result = {}
    for kind, entries in artifacts.items():
        if not entries:
            raise LedgerError("artifact group must not be empty")
        result[kind] = {}
        for name, expected in entries.items():
            path = require_plain_absolute_path(name)
            if not path.is_absolute() or not isinstance(expected, str) or len(expected) != 64:
                raise LedgerError("absolute artifact path and SHA-256 required")
            # Reopen and hash every byte at every boundary. file_digest uses a
            # bounded reusable buffer instead of allocating the entire artifact;
            # no metadata or digest is reused between verifications.
            with path.open('rb') as stream:
                actual = hashlib.file_digest(stream, 'sha256').hexdigest()
            if actual != expected:
                raise LedgerError("artifact hash mismatch")
            result[kind][str(path)] = actual
    return result


def _parse(raw):
    try:
        return json.loads(raw, object_pairs_hook=_unique,
                          parse_constant=lambda _: (_ for _ in ()).throw(LedgerError("nonfinite JSON")))
    except RecursionError:
        raise LedgerError("JSON nesting too deep") from None


def _probability(value):
    if type(value) not in (int, float):
        return False
    try:
        return math.isfinite(value) and 0 <= value <= 1
    except OverflowError:
        return False


def _distribution_total_valid(values):
    # Match CategorySuggestionPolicy.DistributionTotalValid, including its
    # inclusive one-percent tolerance and accumulation rounding allowance.
    # Dictionary.Values uses sequential double addition in Enumerable.Sum.
    # Python 3.12+ sum compensates rounding instead, so accumulate explicitly
    # to keep boundary decisions identical across supported Python versions.
    total = 0.0
    for value in values:
        total += float(value)
    return abs(total - 1.0) <= 0.01 + 1e-12


def validate_envelope(payload, response):
    """Strict wire envelope checks; actual reducers still decide via PairedReplay.

    No local tokenizer is known. Size limits are byte limits, never token estimates.
    Stop if provider-reported usage exceeds the ledger's reservation assumption.
    """
    request, reply = _parse(payload), _parse(response)
    if request.get("model") != MODEL or set(reply) != {"model", "answers", "usage"} or reply["model"] != MODEL:
        raise LedgerError("model or response envelope mismatch")
    usage = reply["usage"]
    if not isinstance(usage, dict) or set(usage) != {"input_tokens", "output_tokens"}:
        raise LedgerError("usage required")
    if any(type(value) is not int or not 0 <= value <= 65_536 for value in usage.values()):
        raise LedgerError("unknown or excessive usage; reservation retained")
    questions, answers = request["questions"], reply["answers"]
    if not isinstance(answers, dict) or set(answers) != set(questions):
        raise LedgerError("answer membership mismatch")
    for name, question in questions.items():
        answer = answers[name]
        if question["type"] == "noul":
            if set(answer) != {"type", "noul"} or answer["type"] != "noul" or not _probability(answer["noul"]):
                raise LedgerError("invalid noul")
        elif question["type"] == "choice":
            if set(answer) != {"type", "choice", "confidence", "probabilities"} or answer["type"] != "choice":
                raise LedgerError("invalid choice")
            probabilities = answer["probabilities"]
            if (not isinstance(probabilities, dict) or set(probabilities) != set(question["criteria"])
                    or answer["choice"] not in probabilities or not _probability(answer["confidence"])
                    or not all(_probability(value) for value in probabilities.values())
                    or not _distribution_total_valid(probabilities.values())
                    or any(value > probabilities[answer["choice"]] + 1e-9
                           for value in probabilities.values())):
                raise LedgerError("invalid choice distribution")
        else:
            raise LedgerError("unsupported question type")
    return usage


def exchange_recorded(ledger, payload, transport, *, artifacts, private_output):
    """Reserve before consuming one synthetic response, persist exact private bytes.

    Any exception leaves a missing outcome and permanently stops subsequent sends
    through this seam, including after restart. Never reconcile/release reservations.
    """
    if not _EXCHANGE_OWNER.acquire(blocking=False):
        raise LedgerBusy("exchange already owned; no retry")
    try:
        return _exchange_recorded_owned(ledger, payload, transport,
                                        artifacts=artifacts, private_output=private_output)
    finally:
        _EXCHANGE_OWNER.release()


def _exchange_recorded_owned(ledger, payload, transport, *, artifacts, private_output, validate_response=None):
    if type(ledger) is not CampaignLedger or type(transport) is not RecordedTransport:
        raise LedgerError("only durable ledger and recorded transport are supported")
    output = require_plain_absolute_path(private_output, allow_missing_leaf=True)
    if not output.is_absolute() or any((parent / ".git").exists() for parent in (output, *output.parents)):
        raise LedgerError("explicit private output outside a checkout required")
    if output.exists():
        raise LedgerError("output must not exist")
    hashes = verify_artifacts(artifacts)
    if ledger.snapshot()["missing_outcomes"]:
        raise LedgerError("unresolved attempt; stop and inspect")
    # Validate request/model before reservation. Nothing consumes the reply yet.
    if type(payload) is not bytes or _parse(payload).get("model") != MODEL:
        raise LedgerError("pinned request bytes required")
    attempt = ledger.reserve_attempt(payload)
    # CreateNew prevents overwrite; failure after reservation also stops campaign.
    output.mkdir()
    response = transport.exchange(payload)
    if not 0 < len(response) <= MAX_RESPONSE_BYTES:
        raise LedgerError("invalid response size")
    for name, data in (("request.json", payload), ("response.json", response)):
        with (output / name).open("xb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
    usage = validate_envelope(payload, response)
    receipt = {"format": "guarded-recorded-exchange-v1", "attempt": attempt,
               "network_calls": 0, "model": MODEL, "artifacts": hashes, "usage": usage,
               "request_sha256": hashlib.sha256(payload).hexdigest(),
               "response_sha256": hashlib.sha256(response).hexdigest()}
    with (output / "receipt.json").open("x", encoding="utf-8") as stream:
        json.dump(receipt, stream, sort_keys=True)
        stream.flush()
        os.fsync(stream.fileno())
    # No success is recorded if any frozen file changed while consuming or
    # persisting the response. The attempt stays unresolved after restart.
    verify_artifacts(artifacts)
    if validate_response is not None:
        validate_response(response)
        verify_artifacts(artifacts)
    ledger.record_outcome(attempt, "success")
    return receipt
