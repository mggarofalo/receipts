"""Strict schema for the authored safety challenge; no transport or credentials.

Inputs: {version: 1, taxonomy: [{id, name, description, parent: id|null}],
         cases: [{id, group, description}]}.
Evaluator labels: [{id, outcome, expected: id|null, reason, rationale}].
Unknown fields are rejected, including evaluator metadata in model inputs.
"""

from collections import Counter
from copy import deepcopy


def _object(value, keys):
    if not isinstance(value, dict) or set(value) != set(keys):
        raise ValueError(f"expected exactly these fields: {', '.join(keys)}")


def _text(value):
    if not isinstance(value, str) or not value.strip():
        raise ValueError("expected nonempty text")


def _rows(rows, keys):
    if not isinstance(rows, list) or not rows:
        raise ValueError("expected a nonempty array")
    indexed = {}
    for row in rows:
        _object(row, keys)
        _text(row["id"])
        if row["id"] in indexed:
            raise ValueError("duplicate id")
        indexed[row["id"]] = row
    return indexed


def _inputs(inputs):
    _object(inputs, ("version", "taxonomy", "cases"))
    if type(inputs["version"]) is not int or inputs["version"] != 1:
        raise ValueError("unsupported fixture version")
    taxonomy = _rows(inputs["taxonomy"], ("id", "name", "description", "parent"))
    for row in taxonomy.values():
        _text(row["name"])
        _text(row["description"])
        visited = {row["id"]}
        parent = row["parent"]
        while parent is not None:
            if not isinstance(parent, str) or parent not in taxonomy or parent in visited:
                raise ValueError("missing parent or taxonomy cycle")
            visited.add(parent)
            parent = taxonomy[parent]["parent"]
    cases = _rows(inputs["cases"], ("id", "group", "description"))
    for row in cases.values():
        _text(row["group"])
        _text(row["description"])
    return taxonomy, cases


def validate_fixture(inputs, labels):
    """Validate the executable schema, taxonomy references, and fixed challenge mix."""
    taxonomy, cases = _inputs(inputs)
    indexed = _rows(labels, ("id", "outcome", "expected", "reason", "rationale"))
    if set(indexed) != set(cases):
        raise ValueError("labels must match cases exactly")
    parents = {row["parent"] for row in taxonomy.values() if row["parent"] is not None}
    for row in labels:
        _text(row["rationale"])
        if row["outcome"] == "abstain":
            if row["expected"] is not None:
                raise ValueError("abstention cannot have a category")
        elif row["outcome"] in ("broad_parent", "assignable"):
            expected = row["expected"]
            if not isinstance(expected, str) or expected not in taxonomy:
                raise ValueError("expected category must exist")
            if (expected in parents) != (row["outcome"] == "broad_parent"):
                raise ValueError("parent/leaf outcome mismatch")
            if row["reason"] != row["outcome"] + "_control":
                raise ValueError("invalid control reason")
        else:
            raise ValueError("invalid outcome")
    if Counter(row["outcome"] for row in labels) != {"abstain": 16, "broad_parent": 3, "assignable": 5}:
        raise ValueError("expected 16 abstentions, 3 broad parents, 5 assignable controls")
    reasons = Counter(row["reason"] for row in labels if row["outcome"] == "abstain")
    if reasons != {"nonpurchase": 4, "opaque": 3, "out_of_taxonomy": 3,
                   "cross_root": 3, "unclear_purpose": 3}:
        raise ValueError("unexpected abstention reason mix")


def model_state(inputs, case_id):
    """Allowlisted model-visible state. Never accepts the labels document.

    IDs/groups are local routing and split metadata, not model state. This
    function only builds data; it cannot send it to Jev or any other provider.
    """
    _, cases = _inputs(inputs)
    if case_id not in cases:
        raise ValueError("unknown case id")
    return {"description": cases[case_id]["description"],
            "taxonomy": deepcopy(inputs["taxonomy"])}
