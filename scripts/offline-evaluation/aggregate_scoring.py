"""Offline aggregate-only scorer for the frozen 24-case paired pilot.

No provider, credentials, implicit data paths, or per-case output. Inputs are
private production cases, adjudicated labels, and OfflineReplay JSON artifacts.
"""

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile

from evaluation import _load, _rate, score


def _require(condition):
    if not condition:
        raise ValueError("invalid or misaligned scoring artifact")


def _index(rows, key):
    _require(isinstance(rows, list))
    result = {}
    for row in rows:
        _require(isinstance(row, dict) and isinstance(row.get(key), str)
                 and bool(row[key].strip()) and row[key] not in result)
        result[row[key]] = row
    return result


def _paths(categories):
    indexed = _index(categories, "id")
    result = {}
    for key in indexed:
        path = []
        current = key
        while current is not None:
            _require(current in indexed and current not in path)
            path.insert(0, current)
            current = indexed[current]["parentId"]
        result[key] = path
    return result


def _oracle(label, paths):
    _require(label.get("status") in ("labelable", "abstain_required", "unresolved"))
    specific, broad = set(), set()
    for field, target in (("maximallySpecificAcceptablePaths", specific),
                          ("otherSupportedCoarseAlternatives", broad)):
        _require(isinstance(label.get(field), list))
        for row in label[field]:
            _require(isinstance(row, dict) and row.get("categoryId") in paths)
            _require(row.get("pathIds") == paths[row["categoryId"]])
            target.add(row["categoryId"])
    _require(isinstance(label.get("validBroadAncestorIds"), list))
    for key in label["validBroadAncestorIds"]:
        _require(isinstance(key, str) and key in paths)
        broad.add(key)
    broad -= specific
    _require((label["status"] == "labelable") == bool(specific or broad))
    _require(type(label.get("initialPassDisagreement")) is bool)
    return specific, broad


def _validate_attempts(cases, replay):
    """Structural checks; the CLI additionally regenerates production replay."""
    _require([c["id"] for c in cases] == [o["id"] for o in replay["outcomes"]])
    attempts = replay["attempts"]
    _require(isinstance(attempts, list) and 1 <= len(attempts) <= 48)
    expected = []
    for outcome in replay["outcomes"]:
        status = outcome["status"]
        if status == "unattempted":
            continue
        baseline = outcome.get("baseline")
        _require(isinstance(baseline, dict))
        stages = ["baseline"]
        if baseline.get("failure") is None:
            suggestion = baseline.get("suggestion")
            _require(isinstance(suggestion, dict))
            if suggestion.get("categoryId") is not None:
                stages.append("selected-path")
            else:
                _require(status == "completed" and outcome.get("treatment") == baseline)
        else:
            _require(status in ("failed", "unknown") and outcome.get("treatment") is None)
        expected.extend((outcome["id"], stage,
                         "unknown" if status == "unknown" and stage == stages[-1] else "replayed")
                        for stage in stages)
    _require(len(attempts) == len(expected))
    for attempt, (case_id, stage, status) in zip(attempts, expected):
        _require(isinstance(attempt, dict) and attempt.get("caseId") == case_id
                 and attempt.get("stage") == stage and attempt.get("status") == status
                 and isinstance(attempt.get("requestJson"), str)
                 and hashlib.sha256(attempt["requestJson"].encode("utf-8")).hexdigest()
                 == attempt.get("requestSha256"))
    _require(sum(a["stage"] == "baseline" for a in attempts) <= 24
             and sum(a["stage"] == "selected-path" for a in attempts) <= 24)
    _require(replay["completedCases"] == sum(o["status"] == "completed" for o in replay["outcomes"])
             and replay["unattemptedCases"] == sum(o["status"] == "unattempted" for o in replay["outcomes"]))
    reservation = len(attempts) * 2_752_512
    _require(replay["reservationNanodollars"] == reservation
             and replay["priorCarryNanodollars"] == 4_750_000_000
             and replay["combinedReservationNanodollars"] == reservation + 4_750_000_000)


def verify_production_replay(cases_path, exchanges_path, runner, replay, scratch):
    """Regenerate all payloads/parsers/decisions in the socket-free .NET runner.

    The built runner identity must be verified against the frozen production
    source before use. Never invoke the live command or trust submitted hashes
    alone. Child output is suppressed because artifacts contain private state.
    """
    _require(runner.suffix.lower() == ".dll" and runner.is_file())
    regenerated = scratch / "regenerated.private.json"
    result = subprocess.run(["dotnet", str(runner.resolve()), "offline-replay",
                             str(cases_path.resolve()), str(exchanges_path.resolve()),
                             str(regenerated.resolve())], stdout=subprocess.DEVNULL,
                            stderr=subprocess.DEVNULL, timeout=120)
    _require(result.returncode == 0 and _load(regenerated) == replay)
    exchanges = _load(exchanges_path)
    _require(isinstance(exchanges, list))
    attempts = replay["attempts"]
    # A missing tail exchange legitimately produces a reserved unknown attempt.
    missing_tail = bool(attempts) and attempts[-1]["status"] == "unknown"
    _require(len(exchanges) == len(attempts) or
             (missing_tail and len(exchanges) == len(attempts) - 1))
    for exchange, attempt in zip(exchanges, attempts):
        _require(exchange.get("requestSha256") == attempt["requestSha256"])


def _outcome(result, status, paths, specific, broad, unresolved):
    if status == "unattempted":
        _require(result is None)
        return "unattempted", False, False
    if result is None:
        _require(status in ("failed", "unknown"))
        return status, False, False
    _require(isinstance(result, dict))
    if result.get("failure") is not None:
        _require(status in ("failed", "unknown") and result.get("suggestion") is None)
        return status, False, False
    suggestion = result.get("suggestion")
    _require(isinstance(suggestion, dict))
    key = suggestion.get("categoryId")
    if key is None:
        _require(suggestion.get("path") == [] and bool(suggestion.get("abstentionReason")))
        return "abstained", False, False
    _require(isinstance(key, str) and key in paths
             and suggestion.get("path") == paths[key]
             and suggestion.get("abstentionReason") is None)
    if unresolved:
        return "unresolved_assignment", False, False
    if key in specific:
        return "most_specific", False, False
    if key in broad:
        return "broad", False, False
    accepted = specific | broad
    # Explicit useful ancestors only: no mechanical credit for other ancestors.
    harmful = any(parent != key and parent in paths[key] for parent in accepted)
    wrong = bool(accepted) and not any(
        key in paths[parent] or parent in paths[key] for parent in accepted)
    return ("harmful_specificity" if harmful else "wrong_branch" if wrong
            else "other_unsupported"), harmful, wrong


def aggregate(cases, labels, replay):
    """Return counts/rates only; retain failures and suffixes in denominator 24."""
    indexed = _index(cases, "id")
    _require(len(indexed) == 24)
    gold = _index(labels["labels"], "caseId")
    _require(set(indexed) <= set(gold))
    paths = _paths(cases[0]["request"]["categories"])
    _require(all(c["request"]["categories"] == cases[0]["request"]["categories"]
                 and c["request"]["taxonomyRevision"] == cases[0]["request"]["taxonomyRevision"]
                 for c in cases))
    outcomes = _index(replay["outcomes"], "id")
    _require(set(indexed) == set(outcomes) and replay["totalCases"] == 24)
    _validate_attempts(cases, replay)
    counts = {arm: Counter() for arm in ("baseline", "treatment")}
    states = {arm: [] for arm in counts}
    transitions = Counter()
    strata = Counter()
    stopped = False
    for key, case in indexed.items():
        label, outcome = gold[key], outcomes[key]
        specific, broad = _oracle(label, paths)
        status = outcome.get("status")
        _require(status in ("completed", "failed", "unknown", "unattempted"))
        _require(not stopped or status == "unattempted")
        _require(status != "unattempted" or stopped)
        stopped |= status in ("failed", "unknown")
        no_answer = label["status"] == "abstain_required"
        unresolved = label["status"] == "unresolved"
        broad_only = bool(specific | broad) and all(
            any(v != p and p in paths[v] for v in paths) for p in specific | broad)
        strata["no_answer"] += no_answer
        strata["broad_only"] += broad_only
        strata["unresolved"] += unresolved
        strata["initial_disagreement"] += label["initialPassDisagreement"]
        strata["uncertainty_marked"] += bool(label.get("uncertainty"))
        pair = []
        for arm in counts:
            state, harmful, wrong = _outcome(outcome.get(arm), status, paths,
                                              specific, broad, unresolved)
            _require(status != "completed" or state not in ("failed", "unknown", "unattempted"))
            pair.append(state)
            useful = state in ("most_specific", "broad")
            write = state in ("most_specific", "broad", "harmful_specificity", "wrong_branch",
                              "other_unsupported", "unresolved_assignment")
            counts[arm][state] += 1
            for name, flag in (("accepted", useful), ("writes", write),
                               ("harmful_specificity_flag", harmful), ("wrong_branch_flag", wrong),
                               ("writes_on_no_answer", write and no_answer),
                               ("writes_on_broad_only", write and broad_only),
                               ("missed_useful", state == "abstained" and bool(specific | broad)),
                               ("correct_no_answer_abstention", state == "abstained" and no_answer),
                               ("accepted_initial_agreement", useful and not label["initialPassDisagreement"]),
                               ("accepted_without_uncertainty", useful and not label.get("uncertainty"))):
                counts[arm][name] += flag
            states[arm].append({"id": key, "status": "answered" if write else
                                "failed" if state == "unknown" else state if state in
                                ("failed", "unattempted", "abstained") else "answered",
                                **({"label": "accepted" if useful else "unsupported"} if write else {})})
        if status in ("failed", "unknown"):
            _require(status in pair)
        if status == "completed":
            baseline = outcome["baseline"]["suggestion"]
            treatment = outcome["treatment"]["suggestion"]
            _require(treatment["categoryId"] is None or
                     (treatment["categoryId"] == baseline["categoryId"]
                      and treatment["path"] == baseline["path"]))
        transitions[" -> ".join(pair)] += 1
        counts["treatment"]["baseline_errors_suppressed"] += (
            pair[0] in ("wrong_branch", "harmful_specificity", "other_unsupported")
            and pair[1] == "abstained")
        counts["treatment"]["useful_baseline_suppressed"] += (
            pair[0] in ("most_specific", "broad") and pair[1] == "abstained")
    _require(bool(replay["stopped"]) == stopped)
    reports = {}
    for arm in counts:
        c = counts[arm]
        for name in ("most_specific", "broad", "wrong_branch", "harmful_specificity",
                     "other_unsupported", "unresolved_assignment", "abstained", "failed",
                     "unknown", "unattempted", "accepted", "writes"):
            c.setdefault(name, 0)
        # Reuse existing cohort accounting; retain unknown separately above.
        base = score([{"id": k, "group": k, "expected": "accepted"} for k in indexed], states[arm])
        reports[arm] = {"counts": dict(c), "useful_coverage": base["accuracy"],
                        "write_coverage": base["coverage"],
                        "selective_accuracy": base["selective_accuracy"],
                        "agreement_only_useful_coverage": _rate(c["accepted_initial_agreement"], 24),
                        "unambiguous_useful_coverage": _rate(c["accepted_without_uncertainty"], 24)}
    return {"total": 24, "oracle": "provisional agent adjudication; not human gold",
            "strata": dict(strata), "arms": reports, "paired_transitions": dict(transitions),
            "selected_path_attempts": sum(a["stage"] == "selected-path" for a in replay["attempts"]),
            "interval_caveat": "95% Wilson; assumes independence; curated families may correlate"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for field in ("cases", "labels", "replay", "freeze", "output", "exchanges", "runner"):
        parser.add_argument("--" + field, type=Path, required=True)
    args = parser.parse_args()
    try:
        freeze = _load(args.freeze)
        _require(freeze["beforeCurrentPredictions"] is True and freeze["cohortSize"] == 24)
        for field, binding in (("cases", "membership"), ("labels", "adjudicatedLabels")):
            _require(hashlib.sha256(getattr(args, field).read_bytes()).hexdigest()
                     == freeze["sha256"][binding])
        replay = _load(args.replay)
        # Temporary raw replay remains next to private output, outside the repo.
        with tempfile.TemporaryDirectory(prefix="scoring-", dir=args.output.parent) as directory:
            verify_production_replay(args.cases, args.exchanges, args.runner, replay, Path(directory))
        report = aggregate(_load(args.cases), _load(args.labels), replay)
        with args.output.open("x", encoding="utf-8") as output:
            output.write(json.dumps(report, indent=2, allow_nan=False) + "\n")
    except (OSError, ValueError, KeyError, TypeError, IndexError, subprocess.SubprocessError):
        # Never print JSON parsing excerpts, private identifiers or filesystem paths.
        parser.exit(2, "aggregate scoring: invalid input or unavailable output\n")


if __name__ == "__main__":
    main()
