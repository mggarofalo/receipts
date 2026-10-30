"""Offline aggregate-only scorer for a frozen paired baseline/selected-path cohort.

No provider, credentials, implicit data paths, or per-case output. Inputs are
private production cases, adjudicated labels, a scoring freeze, and
OfflineReplay JSON artifacts. Cohort size, attempt cap, prior carry, candidate
gates and acceptance fractions come from the validated freeze only.
"""

import argparse
from collections import Counter
from fractions import Fraction
import hashlib
import json
import math
from pathlib import Path
import subprocess
import tempfile

from evaluation import _load, _rate, score

# Inherited reservation assumption: 65,536 input tokens at USD 0.042/million.
PER_ATTEMPT_NANODOLLARS = 2_752_512
MAXIMUM_COHORT = 48
WRITES = ("most_specific", "broad", "sibling_leaf_same_root", "harmful_specificity",
          "wrong_branch", "other_unsupported", "unresolved_assignment")
USEFUL = ("most_specific", "broad")
# A sibling leaf under the owner's root is still not the owner's decision, so it
# stays an incorrect write for the gates; it is reported as its own class.
INCORRECT = ("sibling_leaf_same_root", "harmful_specificity", "wrong_branch",
             "other_unsupported", "unresolved_assignment")
UNTESTED_REASON = (
    "{gates} untested: the cohort has no cases in the corresponding gold stratum. "
    "Owner-derived labels (provenance owner-current-assignment) are always labelable "
    "with one maximally specific path, so they contain no no-answer or broad-only cases; "
    "this is intended. Cover those safety strata with the separate synthetic-safety set. "
    "Overall cannot pass while any gate is untested.")


def _require(condition):
    if not condition:
        raise ValueError("invalid or misaligned scoring artifact")


def _number(value, low, high):
    return (not isinstance(value, bool) and isinstance(value, (int, float))
            and math.isfinite(value) and low <= value <= high)


def _sha256(value):
    return (isinstance(value, str) and len(value) == 64
            and all(c in "0123456789abcdef" for c in value))


def validate_freeze(freeze):
    """Fail closed unless the scoring freeze has exactly the preregistered shape."""
    _require(isinstance(freeze, dict) and set(freeze) == {
        "beforeCurrentPredictions", "cohortSize", "maximumAttempts", "priorCarryNanodollars",
        "candidateGates", "acceptance", "sha256"})
    size = freeze["cohortSize"]
    _require(freeze["beforeCurrentPredictions"] is True
             and type(size) is int and 1 <= size <= MAXIMUM_COHORT
             and type(freeze["maximumAttempts"]) is int and freeze["maximumAttempts"] == 2 * size
             and type(freeze["priorCarryNanodollars"]) is int and freeze["priorCarryNanodollars"] >= 0)
    gates, acceptance, hashes = freeze["candidateGates"], freeze["acceptance"], freeze["sha256"]
    _require(isinstance(gates, dict) and set(gates) == {"globalFit", "selectedPathSupport"}
             and all(_number(v, 0.7, 1) for v in gates.values()))
    _require(isinstance(acceptance, dict)
             and set(acceptance) == {"minimumUsefulFraction", "maximumUsefulSuppressionFraction"}
             and _number(acceptance["minimumUsefulFraction"], 0, 1)
             and acceptance["minimumUsefulFraction"] > 0
             and _number(acceptance["maximumUsefulSuppressionFraction"], 0, 1))
    _require(isinstance(hashes, dict) and set(hashes) == {"membership", "adjudicatedLabels", "policy"}
             and all(_sha256(v) for v in hashes.values()))
    return freeze


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


def _validate_attempts(cases, replay, freeze):
    """Structural checks; the CLI additionally regenerates production replay."""
    _require([c["id"] for c in cases] == [o["id"] for o in replay["outcomes"]])
    attempts = replay["attempts"]
    _require(isinstance(attempts, list) and 1 <= len(attempts) <= freeze["maximumAttempts"])
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
    size = freeze["cohortSize"]
    _require(sum(a["stage"] == "baseline" for a in attempts) <= size
             and sum(a["stage"] == "selected-path" for a in attempts) <= size)
    _require(replay["completedCases"] == sum(o["status"] == "completed" for o in replay["outcomes"])
             and replay["unattemptedCases"] == sum(o["status"] == "unattempted" for o in replay["outcomes"]))
    reservation = len(attempts) * PER_ATTEMPT_NANODOLLARS
    carry = freeze["priorCarryNanodollars"]
    _require(replay["reservationNanodollars"] == reservation
             and replay["priorCarryNanodollars"] == carry
             and replay["combinedReservationNanodollars"] == reservation + carry)


def verify_bindings(freeze, cases_path, labels_path, policy_path):
    """Bind exact case, label and packet-policy bytes to the pre-prediction freeze."""
    for path, binding in ((cases_path, "membership"), (labels_path, "adjudicatedLabels"),
                          (policy_path, "policy")):
        _require(hashlib.sha256(path.read_bytes()).hexdigest() == freeze["sha256"][binding])
    # The freeze may not score a different rule, cohort or budget than the runner used.
    policy = _load(policy_path)
    _require(policy.get("autoWriteGlobalGate") == freeze["candidateGates"]["globalFit"]
             and policy.get("autoWriteSelectedGate") == freeze["candidateGates"]["selectedPathSupport"]
             and policy.get("cases") == freeze["cohortSize"]
             and policy.get("maximumAttempts") == freeze["maximumAttempts"]
             and policy.get("conservativePriorCarryNanodollars") == freeze["priorCarryNanodollars"])


def verify_production_replay(policy_path, cases_path, exchanges_path, runner, replay, scratch):
    """Regenerate all payloads/parsers/decisions in the socket-free .NET runner.

    The built runner identity must be verified against the frozen production
    source before use. Never invoke the live command or trust submitted hashes
    alone. Child output is suppressed because artifacts contain private state.
    """
    _require(runner.suffix.lower() == ".dll" and runner.is_file())
    regenerated = scratch / "regenerated.private.json"
    result = subprocess.run(["dotnet", str(runner.resolve()), "offline-replay",
                             str(policy_path.resolve()), str(cases_path.resolve()),
                             str(exchanges_path.resolve()),
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
    # Harmful specificity means deeper than the owner: below a specific gold, or
    # below a broad gold that has no specific gold beneath it (a broad-only label).
    # A broad gold credited only as the root of a specific gold does not make every
    # leaf under that root "deeper"; such a leaf is a sibling of the owner's leaf.
    harmful = any(parent != key and parent in paths[key] for parent in specific) or any(
        parent != key and parent in paths[key]
        and not any(parent in paths[s] for s in specific) for parent in broad)
    if harmful:
        return "harmful_specificity", True, False
    if any(key in paths[parent] for parent in accepted):
        # An uncredited ancestor of the gold: too coarse, not wrong.
        return "other_unsupported", False, False
    if any(paths[key][0] == paths[parent][0] for parent in accepted):
        return "sibling_leaf_same_root", False, False
    wrong = bool(accepted)
    return ("wrong_branch" if wrong else "other_unsupported"), False, wrong


def _candidate(outcome, treatment_state, freeze):
    """Preregistered two-signal auto-write: baseline global fit AND selected support.

    Non-completed cases mirror the treatment state. Missing, boolean or non-finite
    signals never meet a gate.
    """
    if outcome["status"] != "completed" or treatment_state == "abstained":
        return treatment_state
    gates = freeze["candidateGates"]
    fit = outcome["baseline"]["suggestion"].get("fitProbability")
    support = outcome["treatment"]["suggestion"].get("selectedPathSupport")
    if (_number(fit, gates["globalFit"], math.inf)
            and _number(support, gates["selectedPathSupport"], math.inf)):
        return treatment_state
    return "abstained"


def _gate(passed, tested=True):
    return "untested" if not tested else "pass" if passed else "fail"


def evaluate_gates(arm, baseline, strata, freeze):
    """Preregistered necessary pilot gates for one write arm; never rollout evidence."""
    size, acceptance = freeze["cohortSize"], freeze["acceptance"]
    # Decimal reading of the frozen fraction avoids binary rounding at boundaries.
    minimum = math.ceil(Fraction(str(acceptance["minimumUsefulFraction"])) * size)
    allowed = math.floor(Fraction(str(acceptance["maximumUsefulSuppressionFraction"]))
                         * baseline["accepted"])
    gates = {
        # Every case maps to one state, so no failure/unknown/suffix means all completed.
        "complete": _gate(strata["unresolved"] == 0
                          and arm["failed"] + arm["unknown"] + arm["unattempted"] == 0),
        "minimum_useful": _gate(arm["accepted"] >= minimum),
        "zero_incorrect_writes": _gate(sum(arm[k] for k in INCORRECT) == 0),
        "retention": _gate(arm["useful_baseline_suppressed"] <= allowed),
        "no_answer_safety": _gate(arm["writes_on_no_answer"] == 0, strata["no_answer"] > 0),
        "broad_only_safety": _gate(arm["harmful_specificity_on_broad_only"] == 0,
                                   strata["broad_only"] > 0),
    }
    untested = [name for name, value in gates.items() if value == "untested"]
    overall = ("fail" if "fail" in gates.values() else "incomplete" if untested else "pass")
    return {**gates, "overall": overall,
            "untestedReason": UNTESTED_REASON.format(gates=", ".join(untested)) if untested else None,
            "thresholds": {"minimumUseful": minimum, "maximumUsefulSuppressed": allowed}}


def _written(result):
    if not isinstance(result, dict) or result.get("failure") is not None:
        return None
    suggestion = result.get("suggestion")
    return suggestion.get("categoryId") if isinstance(suggestion, dict) else None


def _root_leaf(key, gold, paths, leaf_scored):
    """(root correct, leaf correct) for one write; leaf only counts when the gold leaf is scored."""
    if key is None:
        return False, False
    return paths[key][0] == paths[gold][0], leaf_scored and key == gold


def aggregate(cases, labels, replay, freeze, placeholder_leaves=()):
    """Return counts/rates only; retain failures and suffixes in the frozen denominator.

    Root and leaf accuracy are reported separately over cases with one specific
    gold. A gold leaf whose name is a placeholder (for example "N/A") is scored at
    its root only and leaves the leaf denominator.
    """
    validate_freeze(freeze)
    size = freeze["cohortSize"]
    indexed = _index(cases, "id")
    _require(len(indexed) == size)
    _require(isinstance(labels, dict) and isinstance(labels.get("oracle"), str)
             and bool(labels["oracle"].strip()))
    gold = _index(labels["labels"], "caseId")
    _require(set(indexed) <= set(gold))
    paths = _paths(cases[0]["request"]["categories"])
    names = {c["id"]: c["name"] for c in cases[0]["request"]["categories"]}
    placeholders = {p.casefold() for p in placeholder_leaves}
    root_leaf = {arm: Counter() for arm in ("baseline", "treatment", "candidate")}
    _require(all(c["request"]["categories"] == cases[0]["request"]["categories"]
                 and c["request"]["taxonomyRevision"] == cases[0]["request"]["taxonomyRevision"]
                 for c in cases))
    outcomes = _index(replay["outcomes"], "id")
    _require(set(indexed) == set(outcomes) and replay["totalCases"] == size)
    _validate_attempts(cases, replay, freeze)
    arms = ("baseline", "treatment", "candidate")
    counts = {arm: Counter() for arm in arms}
    states = {arm: [] for arm in arms}
    transitions = {arm: Counter() for arm in arms[1:]}
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
        judged = {arm: _outcome(outcome.get(arm), status, paths, specific, broad, unresolved)
                  for arm in arms[:2]}
        candidate = _candidate(outcome, judged["treatment"][0], freeze)
        judged["candidate"] = (judged["treatment"] if candidate == judged["treatment"][0]
                               else (candidate, False, False))
        if len(specific) == 1:
            (gold_leaf,) = specific
            leaf_scored = len(paths[gold_leaf]) > 1 and names[gold_leaf].casefold() not in placeholders
            for arm in arms:
                # The candidate arm writes the treatment's category only when its gate passes.
                source = "treatment" if arm == "candidate" else arm
                written = (_written(outcome.get(source))
                           if arm != "candidate" or judged[arm][0] in WRITES else None)
                root_ok, leaf_ok = _root_leaf(written, gold_leaf, paths, leaf_scored)
                root_leaf[arm]["cases"] += 1
                root_leaf[arm]["leaf_scored_cases"] += leaf_scored
                root_leaf[arm]["root_correct"] += root_ok
                root_leaf[arm]["leaf_correct"] += leaf_ok
                root_leaf[arm]["root_writes"] += written is not None
        for arm, (state, harmful, wrong) in judged.items():
            _require(status != "completed" or state not in ("failed", "unknown", "unattempted"))
            useful = state in USEFUL
            write = state in WRITES
            counts[arm][state] += 1
            for name, flag in (("accepted", useful), ("writes", write),
                               ("harmful_specificity_flag", harmful), ("wrong_branch_flag", wrong),
                               ("writes_on_no_answer", write and no_answer),
                               # Includes correct broad writes; the safety gate uses the next count.
                               ("writes_on_broad_only", write and broad_only),
                               ("harmful_specificity_on_broad_only",
                                state == "harmful_specificity" and broad_only),
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
            _require(status in (judged["baseline"][0], judged["treatment"][0]))
        if status == "completed":
            baseline = outcome["baseline"]["suggestion"]
            treatment = outcome["treatment"]["suggestion"]
            _require(treatment["categoryId"] is None or
                     (treatment["categoryId"] == baseline["categoryId"]
                      and treatment["path"] == baseline["path"]))
        before = judged["baseline"][0]
        for arm in transitions:
            after = judged[arm][0]
            transitions[arm][before + " -> " + after] += 1
            counts[arm]["baseline_errors_suppressed"] += (
                before in ("wrong_branch", "harmful_specificity", "sibling_leaf_same_root",
                           "other_unsupported")
                and after == "abstained")
            counts[arm]["useful_baseline_suppressed"] += before in USEFUL and after == "abstained"
    _require(bool(replay["stopped"]) == stopped)
    reports = {}
    for arm in arms:
        c = counts[arm]
        for name in ("most_specific", "broad", "sibling_leaf_same_root", "wrong_branch",
                     "harmful_specificity",
                     "other_unsupported", "unresolved_assignment", "abstained", "failed",
                     "unknown", "unattempted", "accepted", "writes", "writes_on_no_answer",
                     "harmful_specificity_on_broad_only"):
            c.setdefault(name, 0)
        if arm != "baseline":
            c.setdefault("baseline_errors_suppressed", 0)
            c.setdefault("useful_baseline_suppressed", 0)
        # Reuse existing cohort accounting; retain unknown separately above.
        base = score([{"id": k, "group": k, "expected": "accepted"} for k in indexed], states[arm])
        r = root_leaf[arm]
        reports[arm] = {"counts": dict(c), "useful_coverage": base["accuracy"],
                        "root_accuracy": _rate(r["root_correct"], r["cases"]),
                        "root_selective_accuracy": _rate(r["root_correct"], r["root_writes"]),
                        "leaf_accuracy": _rate(r["leaf_correct"], r["leaf_scored_cases"]),
                        "write_coverage": base["coverage"],
                        "selective_accuracy": base["selective_accuracy"],
                        "agreement_only_useful_coverage": _rate(c["accepted_initial_agreement"], size),
                        "unambiguous_useful_coverage": _rate(c["accepted_without_uncertainty"], size)}
    gates = {arm: evaluate_gates(counts[arm], counts["baseline"], strata, freeze)
             for arm in ("candidate", "treatment")}
    return {"total": size, "oracle": labels["oracle"],
            "candidate_gates": dict(freeze["candidateGates"]),
            "strata": {k: strata[k] for k in ("no_answer", "broad_only", "unresolved",
                                              "initial_disagreement", "uncertainty_marked")},
            "arms": reports,
            "paired_transitions": {"baseline -> " + arm: dict(t) for arm, t in transitions.items()},
            "gates": gates,
            "selected_path_attempts": sum(a["stage"] == "selected-path" for a in replay["attempts"]),
            "interval_caveat": "95% Wilson; assumes independence; curated families may correlate"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for field in ("cases", "labels", "policy", "replay", "freeze", "output", "exchanges", "runner"):
        parser.add_argument("--" + field, type=Path, required=True)
    parser.add_argument("--placeholder-leaf", action="append", default=[],
                        help="gold leaf name scored at its root only (repeatable)")
    args = parser.parse_args()
    try:
        freeze = validate_freeze(_load(args.freeze))
        verify_bindings(freeze, args.cases, args.labels, args.policy)
        replay = _load(args.replay)
        # Temporary raw replay remains next to private output, outside the repo.
        with tempfile.TemporaryDirectory(prefix="scoring-", dir=args.output.parent) as directory:
            verify_production_replay(args.policy, args.cases, args.exchanges, args.runner,
                                     replay, Path(directory))
        report = aggregate(_load(args.cases), _load(args.labels), replay, freeze,
                           args.placeholder_leaf)
        with args.output.open("x", encoding="utf-8") as output:
            output.write(json.dumps(report, indent=2, allow_nan=False) + "\n")
    except (OSError, ValueError, KeyError, TypeError, IndexError, AttributeError,
            subprocess.SubprocessError):
        # Never print JSON parsing excerpts, private identifiers or filesystem paths.
        parser.exit(2, "aggregate scoring: invalid input or unavailable output\n")


if __name__ == "__main__":
    main()
