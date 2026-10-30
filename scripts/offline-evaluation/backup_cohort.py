"""Freeze a label-separated Jev evaluation cohort from a SQLite portable backup.

The backup is opened read-only. Labels are the owner's existing decisions: each
item's current taxonomy assignment in the backup. Nothing is inferred by a model
or an agent. Outputs are private and must be written outside any git checkout.
No provider, credential or network access exists here.
"""

import argparse
from collections import Counter, defaultdict
from contextlib import closing
from datetime import datetime, timezone
import hashlib
import json
import os
import shutil
from pathlib import Path
import sqlite3
import subprocess
import tempfile

DERIVATION = "backup-cohort-v1"
# Assignment sources that record the owner's own choice. Automatic sources
# (approved_mapping, template, anything unknown) never become gold.
OWNER_SOURCES = frozenset({"legacy_import", "legacy_write", "manual"})


def _sha(data):
    return hashlib.sha256(data).hexdigest()


def _order_key(seed, value):
    # Same unambiguous seed/value encoding as evaluation.group_split.
    return _sha(json.dumps([seed, value], ensure_ascii=True).encode()), value


def _text_key(description):
    return " ".join(description.casefold().split())


def _connect(backup):
    # immutable=1 forbids journal/WAL creation; mode=ro forbids writes.
    uri = Path(backup).resolve().as_uri() + "?mode=ro&immutable=1"
    return sqlite3.connect(uri, uri=True)


def _json_rows(db, kind):
    return [json.loads(p) for (p,) in db.execute(
        "SELECT payload FROM taxonomy_archive WHERE kind = ?", (kind,))]


def load_taxonomy(db):
    """Return (revision, categories) exactly as production builds a request.

    Mirrors CategoryAssistanceDispatchStore.BuildRequestAsync: active nodes of the
    head revision, ordered by category id, blank definitions fall back to name.
    """
    heads = _json_rows(db, "head")
    if len(heads) != 1:
        raise ValueError("backup requires exactly one taxonomy head")
    revision = heads[0]["RevisionId"].lower()
    nodes = [n for n in _json_rows(db, "node")
             if n["RevisionId"].lower() == revision and not n["Archived"]]
    if not nodes:
        raise ValueError("head revision has no active nodes")
    ids = {n["CategoryId"].lower() for n in nodes}
    if len(ids) != len(nodes) or any(
            n["ParentId"] is not None and n["ParentId"].lower() not in ids for n in nodes):
        raise ValueError("taxonomy is not a closed tree")
    categories = [{
        "id": n["CategoryId"].lower(),
        "name": n["Name"],
        "definition": n["Name"] if not (n["Definition"] or "").strip() else n["Definition"],
        "parentId": None if n["ParentId"] is None else n["ParentId"].lower(),
    } for n in sorted(nodes, key=lambda n: n["CategoryId"].lower())]
    return revision, categories


def paths_for(categories):
    parents = {c["id"]: c["parentId"] for c in categories}
    result = {}
    for key in parents:
        path, current = [], key
        while current is not None:
            if current in path:
                raise ValueError("taxonomy cycle")
            path.insert(0, current)
            current = parents[current]
        result[key] = path
    return result


def load_items(db):
    """Items with their current owner decision. Deleted rows are excluded."""
    deleted = set()
    for (payload,) in db.execute("SELECT payload FROM portable_deletion_state "
                                 "WHERE kind IN ('ReceiptItems', 'Receipts')"):
        row = json.loads(payload)
        if row.get("DeletedAt") is not None:
            deleted.add(row["Id"].lower())
    assignments = {a["Id"].lower(): a for a in _json_rows(db, "assignment")}
    current = {c["ReceiptItemId"].lower(): c["AssignmentId"].lower()
               for c in _json_rows(db, "current")}
    items = []
    for item_id, receipt_id, description, normalized_id in db.execute(
            "SELECT id, receipt_id, description, normalized_description_id FROM receipt_items"):
        item_id, receipt_id = item_id.lower(), receipt_id.lower()
        if item_id in deleted or receipt_id in deleted:
            continue
        assignment = assignments.get(current.get(item_id, ""))
        items.append({
            "id": item_id,
            "description": description,
            "normalizedId": normalized_id.lower() if normalized_id else None,
            "categoryId": assignment["CategoryId"].lower() if assignment and assignment["CategoryId"] else None,
            "ownerDecision": bool(assignment) and assignment.get("Source") in OWNER_SOURCES,
        })
    return items


def families(items):
    """Leakage units: union of exact casefolded text and the app's normalized identity.

    Item codes are deliberately not keys: in this data generic codes chain most
    items into one component. This is lexical separation, not semantic independence.
    """
    parent = {}

    def find(x):
        while parent.setdefault(x, x) != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for item in items:
        node = "i:" + item["id"]
        parent[find(node)] = find("t:" + _text_key(item["description"]))
        if item["normalizedId"]:
            parent[find(node)] = find("n:" + item["normalizedId"])
    grouped = defaultdict(list)
    for item in items:
        grouped[find("i:" + item["id"])].append(item)
    return [sorted(members, key=lambda i: i["id"]) for members in grouped.values()]


def label_for(members, paths):
    """Gold from owner decisions only. Mixed or unmapped decisions are ineligible."""
    decided = Counter(i["categoryId"] for i in members)
    if None in decided:
        return None, "undecided"
    if not all(i["ownerDecision"] for i in members):
        return None, "non_owner"
    if len(decided) != 1:
        return None, "mixed"
    (category,) = decided
    if category not in paths:
        return None, "inactive_category"
    path = paths[category]
    return {
        "status": "labelable",
        "maximallySpecificAcceptablePaths": [{"categoryId": category, "pathIds": path}],
        "otherSupportedCoarseAlternatives": [],
        # Root credit only: YNAB budgets map to top-level categories, so a root
        # write agrees with the owner's decision. Intermediate ancestors get none.
        "validBroadAncestorIds": [path[0]] if len(path) > 1 else [],
        "initialPassDisagreement": False,
        "provenance": "owner-current-assignment",
        "decisionCount": len(members),
    }, "eligible"


def exposure_keys(members):
    """Hashed text and normalized-identity keys; a later backup may grow a family."""
    keys = {"t:" + _text_key(i["description"]) for i in members}
    keys |= {"n:" + i["normalizedId"] for i in members if i["normalizedId"]}
    return sorted(_sha(k.encode()) for k in keys)


def representative(members, seed):
    counts = Counter(i["description"] for i in members)
    return min(counts, key=lambda d: (-counts[d], _order_key(seed, d)))


def build(backup, seed, size, exclude=()):
    if not isinstance(seed, str) or not seed or size < 1:
        raise ValueError("seed and positive size required")
    with closing(_connect(backup)) as db:
        revision, categories = load_taxonomy(db)
        items = load_items(db)
    paths = paths_for(categories)
    excluded = set(exclude)  # consumed exposure keys
    pool, reasons = [], Counter()
    for members in families(items):
        key = _sha("\n".join(i["id"] for i in members).encode())
        label, reason = label_for(members, paths)
        reasons[reason] += 1
        if label is None:
            continue
        if excluded.intersection(exposure_keys(members)):
            reasons["excluded_consumed"] += 1
            continue
        pool.append((key, members, label))
    pool.sort(key=lambda row: _order_key(seed, row[0]))
    if len(pool) < size:
        raise ValueError("not enough eligible families")
    cases, labels, membership = [], [], []
    for key, members, label in pool[:size]:
        case_id = "case-" + _sha(json.dumps([seed, key]).encode())[:16]
        cases.append({"id": case_id, "request": {
            "description": representative(members, seed),
            "taxonomyRevision": revision, "categories": categories}})
        labels.append({"caseId": case_id, **label})
        membership.append({"caseId": case_id, "familyKey": key,
                           "exposureKeys": exposure_keys(members), "itemCount": len(members),
                           "distinctDescriptions": len({i["description"] for i in members})})
    stats = {
        "items": len(items),
        "families": sum(reasons[r] for r in ("eligible", "mixed", "undecided", "non_owner",
                                             "inactive_category")),
        "familyOutcomes": dict(sorted(reasons.items())),
        "eligiblePool": len(pool),
        "selected": size,
        "taxonomyNodes": len(categories),
        "taxonomyRoots": sum(c["parentId"] is None for c in categories),
        "selectedLabelDepth": dict(sorted(Counter(
            len(l["maximallySpecificAcceptablePaths"][0]["pathIds"]) for l in labels).items())),
    }
    return cases, labels, membership, stats


PER_ATTEMPT_NANODOLLARS = 2_752_512  # 65,536 input tokens at USD 0.042/million
CEILING_NANODOLLARS = 5_000_000_000  # owner's cumulative USD 5 authorization
# Production sources whose exact bytes the C# packet validation compares.
PRODUCTION_SOURCES = (
    "src/Application/Classification/CategoryAssistanceDispatcher.cs",
    "src/Application/Classification/CategorySuggestion.cs",
    "src/Infrastructure/Jev/JevCategoryAssistanceSelectedProvider.cs",
    "src/Infrastructure/Jev/JevSelectedPathQuestions.cs",
    "src/Infrastructure/Jev/JevSuggestionProvider.cs",
    "src/Infrastructure/Jev/JevSuggestionQuestions.cs",
)
ORACLE = ("owner decisions: every item in the family carries the same current assignment "
          "in the backup; mixed or undecided families are excluded; no model or agent labels")


def policy_for(size, prior_carry):
    """Preregistered run policy. Gates were tuned on the consumed development cases."""
    attempts = 2 * size
    if not 1 <= size <= 48 or prior_carry < 0 or (
            prior_carry + attempts * PER_ATTEMPT_NANODOLLARS > CEILING_NANODOLLARS):
        raise ValueError("policy exceeds the cumulative authorization")
    return {
        "packetKind": "owner-labels-v1", "model": "jev-1.14.0", "cases": size,
        "maximumAttempts": attempts, "reservationNanodollarsPerAttempt": PER_ATTEMPT_NANODOLLARS,
        "conservativePriorCarryNanodollars": prior_carry,
        "cumulativeCeilingNanodollars": CEILING_NANODOLLARS,
        "baselineGate": 0.7, "autoWriteGlobalGate": 0.9, "autoWriteSelectedGate": 0.8,
        "concurrency": 1, "retries": 0, "liveAuthorized": False,
    }


def _inside_git(path):
    return any((p / ".git").exists() for p in (path, *path.parents))


def _encode(value):
    return (json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False) + "\n").encode()


def _write_bytes(path, data):
    with path.open("xb") as stream:
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    return _sha(data)


def _write(path, value):
    return _write_bytes(path, _encode(value))


def _production_sources(repository):
    """Committed source bytes and their commit; a dirty pinned file is refused."""
    def git(*args):
        return subprocess.run(["git", "-C", str(repository), *args], check=True,
                              capture_output=True).stdout
    commit = git("rev-parse", "HEAD").decode().strip()
    git("ls-files", "--error-unmatch", "--", *PRODUCTION_SOURCES)  # tracked, not ignored
    if len(commit) != 40 or git("status", "--porcelain", "--", *PRODUCTION_SOURCES):
        raise ValueError("pinned production sources must be committed and clean")
    return commit, {source: (repository / source).read_bytes() for source in PRODUCTION_SOURCES}


def write_packet(out, repository, seed, size, prior_carry, backup_sha, excluded, built):
    """Runner packet (no labels) and a separate label directory, created atomically."""
    policy = policy_for(size, prior_carry)
    # Read every input before creating anything, so a failure leaves no partial packet.
    commit, sources = _production_sources(repository)
    if out.exists():
        raise FileExistsError("output exists")
    # Stage beside the target and rename at the end: a failure never leaves a
    # packet at the final path, and the private staging directory is removed.
    out.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    stage = Path(tempfile.mkdtemp(prefix="." + out.name + ".", dir=out.parent))
    try:
        _write_packet_files(stage, commit, sources, policy, seed, size, prior_carry,
                            backup_sha, excluded, built)
        os.rename(stage, out)
    except BaseException:
        shutil.rmtree(stage, ignore_errors=True)
        raise


def _write_packet_files(out, commit, sources, policy, seed, size, prior_carry, backup_sha,
                        excluded, built):
    cases, labels, membership, stats = built
    packet, label_dir = out / "packet", out / "labels"
    for directory in (packet, packet / "production-source", label_dir):
        directory.mkdir(mode=0o700)
    files = {"cases.private.json": _write(packet / "cases.private.json", cases),
             "policy.private.json": _write(packet / "policy.private.json", policy)}
    for source in PRODUCTION_SOURCES:
        name = "production-source/" + Path(source).name
        files[name] = _write_bytes(packet / name, sources[source])
    created = datetime.now(timezone.utc).isoformat()
    _write(packet / "freeze.private.json", {
        "sourceCommit": commit, "createdUtc": created,
        "derivation": DERIVATION, "providerCalls": 0, "filesSha256": files})
    labels_sha = _write(label_dir / "labels.private.json", {"oracle": ORACLE, "labels": labels})
    _write(label_dir / "scoring-freeze.private.json", {
        "beforeCurrentPredictions": True, "cohortSize": size,
        "maximumAttempts": policy["maximumAttempts"], "priorCarryNanodollars": prior_carry,
        "candidateGates": {"globalFit": policy["autoWriteGlobalGate"],
                           "selectedPathSupport": policy["autoWriteSelectedGate"]},
        "acceptance": {"minimumUsefulFraction": 0.5, "maximumUsefulSuppressionFraction": 0.1},
        "sha256": {"membership": files["cases.private.json"], "adjudicatedLabels": labels_sha,
                   "policy": files["policy.private.json"]}})
    membership_sha = _write(out / "membership.private.json", membership)
    _write(out / "manifest.private.json", {
        "derivation": DERIVATION, "seed": seed, "size": size, "backupSha256": backup_sha,
        "taxonomyRevision": cases[0]["request"]["taxonomyRevision"],
        "excludedMemberships": excluded, "membershipSha256": membership_sha,
        "labelsSha256": labels_sha, "packetFilesSha256": files, "stats": stats})


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--backup", type=Path, required=True)
    parser.add_argument("--repository", type=Path, required=True,
                        help="checkout whose production sources the packet pins")
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--seed", required=True)
    parser.add_argument("--size", type=int, required=True)
    parser.add_argument("--prior-carry-nanodollars", type=int, required=True,
                        help="conservative reservation for all earlier or uncertain spend")
    parser.add_argument("--exclude", type=Path, action="append", default=[],
                        help="earlier membership.private.json whose families are consumed")
    args = parser.parse_args(argv)
    try:
        out = args.output_dir.resolve()
        if _inside_git(out):
            raise ValueError("private output must be outside any git checkout")
        policy_for(args.size, args.prior_carry_nanodollars)
        exclude = []
        for path in args.exclude:
            for row in json.loads(path.read_text("utf-8")):
                exclude.extend(row["exposureKeys"])
        built = build(args.backup, args.seed, args.size, exclude)
        write_packet(out, args.repository.resolve(), args.seed, args.size,
                     args.prior_carry_nanodollars, _sha(args.backup.read_bytes()),
                     [_sha(p.read_bytes()) for p in args.exclude], built)
        # Counts only: never descriptions, identifiers or category names.
        print(json.dumps(built[3], indent=2))
    except (OSError, ValueError, KeyError, TypeError, sqlite3.Error, json.JSONDecodeError,
            subprocess.CalledProcessError):
        parser.exit(2, "backup cohort: invalid input or unavailable output\n")


if __name__ == "__main__":
    main()
