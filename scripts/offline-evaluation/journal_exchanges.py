"""Extract exact recorded exchanges offline; preserve the original private journal."""

import argparse
import json
from pathlib import Path

from aggregate_scoring import PER_ATTEMPT_NANODOLLARS, _require
from evaluation import _load

# Campaign ledger per-journal maximum (campaign_ledger.Policy.max_calls).
MAXIMUM_JOURNAL_ATTEMPTS = 96


def extract(rows, replay):
    _require(isinstance(rows, list) and bool(rows) and rows[0].get("kind") == "header"
             and isinstance(rows[0].get("identity"), str) and bool(rows[0]["identity"]))
    attempts = replay["attempts"]
    _require(isinstance(attempts, list) and 1 <= len(attempts) <= MAXIMUM_JOURNAL_ATTEMPTS)
    exchanges = []
    cursor = 1
    for index, attempt in enumerate(attempts):
        _require(cursor < len(rows))
        intent = rows[cursor]
        digest = attempt["requestSha256"]
        _require(isinstance(digest, str) and len(digest) == 64
                 and all(c in "0123456789abcdef" for c in digest)
                 and intent.get("kind") == "intent" and type(intent.get("index")) is int
                 and intent["index"] == index and intent.get("requestSha256") == digest
                 and intent.get("reservationNanodollars") == PER_ATTEMPT_NANODOLLARS)
        cursor += 1
        if cursor == len(rows):
            _require(index == len(attempts) - 1 and attempt.get("status") == "unknown")
            break
        response = rows[cursor]
        _require(attempt.get("status") == "replayed" and response.get("kind") == "response"
                 and type(response.get("index")) is int and response["index"] == index
                 and response.get("requestSha256") == digest
                 and isinstance(response.get("body"), str)
                 and type(response.get("statusCode")) is int
                 and 100 <= response["statusCode"] <= 599)
        exchanges.append({k: response[k] for k in ("body", "statusCode", "requestSha256")})
        cursor += 1
    _require(cursor == len(rows))
    return exchanges


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("journal", "replay", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    try:
        _require(args.journal.stat().st_size <= 64 * 1024 * 1024)
        raw = args.journal.read_bytes()
        _require(raw.endswith(b"\n"))
        rows = [json.loads(line) for line in raw.decode("utf-8").splitlines() if line]
        exchanges = extract(rows, _load(args.replay))
        with args.output.open("x", encoding="utf-8") as output:
            output.write(json.dumps(exchanges, ensure_ascii=False, allow_nan=False) + "\n")
    except (OSError, ValueError, KeyError, TypeError, IndexError, AttributeError):
        parser.exit(2, "journal extraction: invalid input or unavailable output\n")


if __name__ == "__main__":
    main()
