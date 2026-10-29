"""Offline evaluation primitives. No provider SDK, transport, or implicit data paths."""

import argparse
import hashlib
import json
import math
from pathlib import Path


def _text(value):
    return isinstance(value, str) and bool(value.strip())


def _cases(rows):
    if not isinstance(rows, list):
        raise ValueError("cases must be a JSON array")
    indexed = {}
    for row in rows:
        if not isinstance(row, dict) or any(not _text(row.get(key)) for key in ("id", "group", "expected")):
            raise ValueError("each case requires nonempty string id, group, and expected")
        if row["id"] in indexed:
            raise ValueError("duplicate case id")
        indexed[row["id"]] = row
    return indexed


def group_split(cases, holdout_fraction, seed):
    """Return a stable manifest, keeping all members of each group together."""
    indexed = _cases(cases)
    if (isinstance(holdout_fraction, bool) or not isinstance(holdout_fraction, (int, float))
            or not 0 < holdout_fraction < 1 or not math.isfinite(holdout_fraction)):
        raise ValueError("holdout_fraction must be finite and between zero and one")
    if not isinstance(seed, str):
        raise ValueError("seed must be a string")
    groups = {row["group"] for row in cases}
    if len(groups) < 2:
        raise ValueError("a disjoint split requires at least two groups")
    # JSON encoding makes the seed/group boundary unambiguous. Hash order avoids
    # dependence on input order or the implementation of a PRNG's shuffle.
    ordered = sorted(groups, key=lambda group: (
        hashlib.sha256(json.dumps([seed, group], ensure_ascii=True).encode()).hexdigest(), group))
    count = min(len(groups) - 1, max(1, math.ceil(len(groups) * holdout_fraction)))
    holdout = set(ordered[:count])
    return [{"id": key, "group": indexed[key]["group"],
             "split": "holdout" if indexed[key]["group"] in holdout else "development"}
            for key in sorted(indexed)]


def validate_probabilities(probabilities, candidates):
    """Validate a normalized candidate distribution; never coerce malformed answers."""
    if (not isinstance(candidates, (list, tuple)) or not candidates
            or any(not _text(candidate) for candidate in candidates)
            or len(set(candidates)) != len(candidates)):
        raise ValueError("candidates must be unique nonempty strings")
    if not isinstance(probabilities, dict) or set(probabilities) != set(candidates):
        raise ValueError("probability keys must exactly match candidates")
    values = list(probabilities.values())
    if any(isinstance(value, bool) or not isinstance(value, (int, float))
           or not 0 <= value <= 1 or not math.isfinite(value) for value in values):
        raise ValueError("probabilities must be finite numbers in [0, 1]")
    if not math.isclose(math.fsum(values), 1, rel_tol=0, abs_tol=1e-6):
        raise ValueError("probabilities must sum to one within 1e-6")
    return dict(probabilities)


def wilson(successes, total):
    """Approximate 95% Wilson interval for a binomial proportion, or None if empty."""
    if (type(successes) is not int or type(total) is not int
            or not 0 <= successes <= total):
        raise ValueError("counts must be integers with 0 <= successes <= total")
    if total == 0:
        return None
    z = 1.959963984540054
    rate = successes / total
    denominator = 1 + z * z / total
    center = (rate + z * z / (2 * total)) / denominator
    radius = z * math.sqrt(rate * (1 - rate) / total + z * z / (4 * total * total)) / denominator
    return [max(0, center - radius), min(1, center + radius)]


def _rate(successes, total):
    return {"value": successes / total if total else None,
            "numerator": successes, "denominator": total, "interval95": wilson(successes, total)}


def score(cases, predictions):
    """Score the full supplied cohort; omitted predictions remain unattempted."""
    indexed = _cases(cases)
    if not isinstance(predictions, list):
        raise ValueError("predictions must be a JSON array")
    counts = {"total": len(indexed), "answered": 0, "correct": 0,
              "abstained": 0, "failed": 0, "unattempted": 0}
    seen = set()
    for prediction in predictions:
        if not isinstance(prediction, dict) or not _text(prediction.get("id")):
            raise ValueError("each prediction requires a nonempty string id")
        key = prediction["id"]
        if key not in indexed or key in seen:
            raise ValueError("unknown or duplicate prediction id")
        status = prediction.get("status")
        if status not in ("answered", "abstained", "failed", "unattempted"):
            raise ValueError("invalid prediction status")
        if status == "answered":
            if not _text(prediction.get("label")):
                raise ValueError("answered predictions require a nonempty label")
            counts["correct"] += prediction["label"] == indexed[key]["expected"]
        elif "label" in prediction:
            raise ValueError("only answered predictions may contain a label")
        seen.add(key)
        counts[status] += 1
    counts["unattempted"] += len(indexed) - len(seen)
    return {"counts": counts,
            "accuracy": _rate(counts["correct"], counts["total"]),
            "coverage": _rate(counts["answered"], counts["total"]),
            "selective_accuracy": _rate(counts["correct"], counts["answered"]),
            "interval_method": "95% Wilson; assumes independent cases, not group-adjusted"}


def _load(path):
    def reject_constant(_):
        raise ValueError("non-finite JSON number")

    def unique_keys(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate JSON object key")
            result[key] = value
        return result

    return json.loads(path.read_text(encoding="utf-8"), parse_constant=reject_constant,
                      object_pairs_hook=unique_keys)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    split_parser = commands.add_parser("split")
    split_parser.add_argument("--holdout-fraction", type=float, required=True)
    split_parser.add_argument("--seed", required=True)
    score_parser = commands.add_parser("score")
    score_parser.add_argument("--predictions", type=Path, required=True)
    for command in (split_parser, score_parser):
        command.add_argument("--cases", type=Path, required=True)
        command.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        cases = _load(args.cases)
        result = (group_split(cases, args.holdout_fraction, args.seed) if args.command == "split"
                  else score(cases, _load(args.predictions)))
        # Exclusive creation prevents accidentally overwriting inputs or earlier evidence.
        with args.output.open("x", encoding="utf-8") as output:
            output.write(json.dumps(result, indent=2, allow_nan=False) + "\n")
    except (OSError, ValueError) as error:
        parser.exit(2, f"evaluation: {error}\n")


if __name__ == "__main__":
    main()
