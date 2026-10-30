import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import random
import sqlite3
import stat
import subprocess
import tempfile
import unittest
from unittest import mock

import aggregate_scoring

import backup_cohort
from backup_cohort import (CEILING_NANODOLLARS, PER_ATTEMPT_NANODOLLARS, PRODUCTION_SOURCES,
                           build, families, load_taxonomy, main, policy_for)

REVISION = "REV-B"


def node(category, parent=None, name=None, definition="", archived=False, revision=REVISION):
    return {"RevisionId": revision, "CategoryId": category, "ParentId": parent,
            "Name": name or category.title(), "Definition": definition, "Archived": archived}


# Mixed-case ids: raw sort would put upper case first; production orders by lowercase id.
TAXONOMY = [
    node("B-FRUIT", definition="Fresh fruit"),
    node("C-citrus", "B-FRUIT", definition="Citrus fruit"),
    node("G-LEMON", "c-CITRUS", name="Lemon", definition="  "),
    node("a-hardware", definition="Hardware"),
    node("d-bolts", "A-HARDWARE", definition="Bolts"),
    node("E-ARCHIVED", archived=True, definition="Retired shelf"),
    node("F-OLD", revision="rev-a", definition="Old revision only"),
]


class Backup:
    """Synthetic portable backup with only the tables backup_cohort reads."""

    def __init__(self, nodes=TAXONOMY, heads=(REVISION,)):
        self.nodes = list(nodes)
        self.heads = list(heads)
        self.items = []
        self.assignments = []
        self.current = []
        self.deletions = []

    def item(self, item_id, description, category=None, normalized=None, receipt="R-1",
             source="legacy_import"):
        self.items.append((item_id, receipt, description, normalized))
        if category is not None:
            assignment = "ASG-" + item_id
            self.assignments.append({"Id": assignment, "CategoryId": category, "Source": source})
            self.current.append({"ReceiptItemId": item_id, "AssignmentId": assignment})
        return self

    def delete(self, table, row_id, deleted_at="2026-01-01T00:00:00Z"):
        self.deletions.append((table, {"Table": table, "Id": row_id, "DeletedAt": deleted_at}))
        return self

    def write(self, path, shuffle=None):
        rows = list(self.items)
        archive = ([("head", {"Id": 1, "RevisionId": h}) for h in self.heads]
                   + [("node", n) for n in self.nodes]
                   + [("assignment", a) for a in self.assignments]
                   + [("current", c) for c in self.current])
        if shuffle is not None:
            random.Random(shuffle).shuffle(rows)
            random.Random(shuffle).shuffle(archive)
        db = sqlite3.connect(path)
        try:
            db.execute("CREATE TABLE receipt_items (id TEXT PRIMARY KEY, receipt_id TEXT, "
                       "description TEXT, normalized_description_id TEXT, quantity INTEGER)")
            db.execute("CREATE TABLE taxonomy_archive (kind TEXT, id TEXT, payload TEXT)")
            db.execute("CREATE TABLE portable_deletion_state (kind TEXT, id TEXT, payload TEXT)")
            db.executemany("INSERT INTO receipt_items VALUES (?, ?, ?, ?, 1)", rows)
            db.executemany("INSERT INTO taxonomy_archive VALUES (?, ?, ?)",
                           [(k, str(i), json.dumps(p)) for i, (k, p) in enumerate(archive)])
            db.executemany("INSERT INTO portable_deletion_state VALUES (?, ?, ?)",
                           [(k, p["Id"], json.dumps(p)) for k, p in self.deletions])
            db.commit()
        finally:
            db.close()
        return path


def many_families(count=24):
    backup = Backup()
    leaves = ["a-hardware", "d-bolts", "B-FRUIT", "c-citrus", "g-lemon"]
    for i in range(count):
        backup.item(f"ITEM-{i:03d}", f"Widget {i:03d}", leaves[i % len(leaves)])
    return backup


def sha_file(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def selected_keys(membership):
    return [row["familyKey"] for row in membership]


def exposure(membership):
    return [key for row in membership for key in row["exposureKeys"]]


class BackupCohortTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.assertFalse(backup_cohort._inside_git(self.root.resolve()),
                         "temp dir must not be inside a git checkout")

    def path(self, name="backup.sqlite"):
        return self.root / name

    def taxonomy(self, backup):
        self.counter = getattr(self, "counter", 0) + 1
        db = sqlite3.connect(backup.write(self.path(f"taxonomy-{self.counter}.sqlite")))
        try:
            return load_taxonomy(db)
        finally:
            db.close()

    # --- taxonomy ---------------------------------------------------------------

    def test_taxonomy_mirrors_production_request(self):
        revision, categories = self.taxonomy(Backup())
        self.assertEqual(revision, "rev-b")
        self.assertEqual([c["id"] for c in categories],
                         ["a-hardware", "b-fruit", "c-citrus", "d-bolts", "g-lemon"])
        by_id = {c["id"]: c for c in categories}
        self.assertNotIn("e-archived", by_id)
        self.assertNotIn("f-old", by_id)
        self.assertEqual(by_id["g-lemon"], {"id": "g-lemon", "name": "Lemon",
                                            "definition": "Lemon", "parentId": "c-citrus"})
        self.assertEqual(by_id["b-fruit"]["definition"], "Fresh fruit")
        self.assertIsNone(by_id["b-fruit"]["parentId"])
        self.assertEqual(by_id["d-bolts"]["parentId"], "a-hardware")

    def test_null_definition_falls_back_to_name(self):
        _, categories = self.taxonomy(Backup([node("x-nuts", name="Nuts", definition=None)]))
        self.assertEqual(categories[0]["definition"], "Nuts")

    def test_open_or_ambiguous_taxonomy_is_rejected(self):
        invalid = {
            "parent archived": [node("p-pear", "E-ARCHIVED"), node("E-ARCHIVED", archived=True)],
            "parent in other revision": [node("p-pear", "F-OLD"), node("F-OLD", revision="rev-a")],
            "parent missing": [node("p-pear", "z-missing")],
            "duplicate id": [node("p-pear"), node("P-PEAR")],
            "no active nodes": [node("p-pear", archived=True)],
        }
        for label, nodes in invalid.items():
            with self.subTest(label), self.assertRaises(ValueError):
                self.taxonomy(Backup(nodes))
        for heads in ((), (REVISION, "rev-a")):
            with self.subTest(heads=heads), self.assertRaises(ValueError):
                self.taxonomy(Backup(heads=heads))

    # --- families and labels ----------------------------------------------------

    def test_families_union_text_and_normalized_identity(self):
        items = [
            {"id": "i1", "description": "Green Apple", "normalizedId": None},
            {"id": "i2", "description": "  green\tAPPLE ", "normalizedId": None},
            {"id": "i3", "description": "Apple, green", "normalizedId": "n1"},
            {"id": "i4", "description": "Granny Smith", "normalizedId": "n1"},
            {"id": "i5", "description": "GREEN apple", "normalizedId": "n1"},  # bridges text and n1
            {"id": "i6", "description": "Hex Bolt", "normalizedId": "n3"},
            {"id": "i7", "description": "Green Apples", "normalizedId": None},
        ]
        grouped = sorted([[i["id"] for i in f] for f in families(items)])
        self.assertEqual(grouped, [["i1", "i2", "i3", "i4", "i5"], ["i6"], ["i7"]])

    def test_normalized_ids_are_case_insensitive_in_backup(self):
        backup = (Backup().item("I-1", "Bartlett pear", "b-fruit", normalized="NORM-1")
                  .item("I-2", "Pear, bartlett", "B-FRUIT", normalized="norm-1"))
        cases, _, membership, stats = build(backup.write(self.path()), "s", 1)
        self.assertEqual(stats["familyOutcomes"], {"eligible": 1})
        self.assertEqual(membership[0]["itemCount"], 2)
        self.assertEqual(membership[0]["distinctDescriptions"], 2)

    def test_mixed_undecided_and_inactive_families_are_ineligible_and_counted(self):
        backup = (Backup()
                  .item("I-1", "Lemon bag", "g-lemon")
                  .item("I-2", "lemon BAG", "c-citrus")           # mixed with I-1
                  .item("I-3", "Mystery screw")                     # no assignment: non_owner
                  .item("I-4", "Wood screw", "d-bolts")
                  .item("I-5", "wood screw", None)                  # undecided beats decided
                  .item("I-6", "Retired gizmo", "E-ARCHIVED")       # assignment to inactive node
                  .item("I-7", "Hex bolt", "D-BOLTS"))
        backup.assignments.append({"Id": "ASG-NULL", "CategoryId": None,
                                    "Source": "manual"})
        backup.current.append({"ReceiptItemId": "I-5", "AssignmentId": "ASG-NULL"})
        _, labels, membership, stats = build(backup.write(self.path()), "s", 1)
        self.assertEqual(stats["familyOutcomes"],
                         {"eligible": 1, "inactive_category": 1, "mixed": 1, "undecided": 2})
        self.assertEqual(stats["families"], 5)
        self.assertEqual(stats["eligiblePool"], 1)
        self.assertEqual(stats["items"], 7)
        self.assertEqual(labels[0]["maximallySpecificAcceptablePaths"][0]["categoryId"],
                         "d-bolts")
        with self.assertRaises(ValueError):
            build(self.path(), "s", 2)

    def test_only_owner_sources_count_as_decisions(self):
        backup = (Backup()
                  .item("I-1", "Brass hinge", "d-bolts", source="manual")
                  .item("I-2", "Steel hinge", "d-bolts", source="legacy_write")
                  .item("I-3", "Iron hinge", "d-bolts", source="legacy_import")
                  .item("I-4", "Copper hinge", "d-bolts", source="approved_mapping")
                  .item("I-5", "Zinc hinge", "d-bolts", source="template")
                  .item("I-6", "Tin hinge", "d-bolts", source=None)
                  .item("I-7", "Lime wedge", "c-citrus", source="manual")
                  .item("I-8", "lime  WEDGE", "c-citrus", source="template"))  # one bad apple
        backup.assignments[5].pop("Source")  # Source missing entirely
        cases, _, _, stats = build(backup.write(self.path()), "s", 3)
        self.assertEqual(stats["familyOutcomes"], {"eligible": 3, "non_owner": 4})
        self.assertEqual(stats["families"], 7)
        self.assertEqual(sorted(c["request"]["description"] for c in cases),
                         ["Brass hinge", "Iron hinge", "Steel hinge"])
        with self.assertRaises(ValueError):
            build(self.path(), "s", 4)

    def test_label_shape_for_root_child_and_grandchild(self):
        backup = (Backup().item("I-1", "Claw hammer", "A-HARDWARE")
                  .item("I-2", "Carriage bolt", "d-bolts")
                  .item("I-3", "Meyer lemon", "G-Lemon")
                  .item("I-4", "meyer  lemon", "g-lemon"))
        cases, labels, membership, stats = build(backup.write(self.path()), "seed", 3)
        by_category = {l["maximallySpecificAcceptablePaths"][0]["categoryId"]: l
                       for l in labels}
        self.assertEqual(by_category["a-hardware"], {
            "caseId": by_category["a-hardware"]["caseId"],
            "status": "labelable",
            "maximallySpecificAcceptablePaths": [{"categoryId": "a-hardware",
                                                  "pathIds": ["a-hardware"]}],
            "otherSupportedCoarseAlternatives": [],
            "validBroadAncestorIds": [],
            "initialPassDisagreement": False,
            "provenance": "owner-current-assignment",
            "decisionCount": 1,
        })
        bolts = by_category["d-bolts"]
        self.assertEqual(bolts["maximallySpecificAcceptablePaths"][0]["pathIds"],
                         ["a-hardware", "d-bolts"])
        self.assertEqual(bolts["validBroadAncestorIds"], ["a-hardware"])
        lemon = by_category["g-lemon"]
        self.assertEqual(lemon["maximallySpecificAcceptablePaths"][0]["pathIds"],
                         ["b-fruit", "c-citrus", "g-lemon"])
        self.assertEqual(lemon["validBroadAncestorIds"], ["b-fruit"])  # root only
        self.assertEqual(lemon["decisionCount"], 2)
        self.assertEqual(stats["selectedLabelDepth"], {1: 1, 2: 1, 3: 1})
        self.assertEqual([c["id"] for c in cases], [l["caseId"] for l in labels])
        self.assertEqual([m["caseId"] for m in membership], [c["id"] for c in cases])
        request = cases[0]["request"]
        self.assertEqual(request["taxonomyRevision"], "rev-b")
        self.assertEqual(len(request["categories"]), 5)

    def test_representative_is_most_frequent_description(self):
        backup = (Backup().item("I-1", "Navel orange", "c-citrus")
                  .item("I-2", "NAVEL ORANGE", "c-citrus")
                  .item("I-3", "NAVEL ORANGE", "c-citrus"))
        cases, _, _, _ = build(backup.write(self.path()), "s", 1)
        self.assertEqual(cases[0]["request"]["description"], "NAVEL ORANGE")

    # --- selection --------------------------------------------------------------

    def test_selection_is_deterministic_seeded_and_order_independent(self):
        backup = many_families()
        first = build(backup.write(self.path("a.sqlite")), "alpha", 6)
        again = build(self.path("a.sqlite"), "alpha", 6)
        shuffled = build(backup.write(self.path("b.sqlite"), shuffle=7), "alpha", 6)
        other = build(self.path("a.sqlite"), "beta", 6)
        self.assertEqual(first, again)
        self.assertEqual(first, shuffled)
        self.assertNotEqual(set(selected_keys(first[2])), set(selected_keys(other[2])))
        self.assertEqual(len(set(selected_keys(first[2]))), 6)

    def test_exclude_removes_consumed_families(self):
        backup = many_families(10)
        _, _, earlier, _ = build(backup.write(self.path()), "alpha", 4)
        consumed = exposure(earlier)
        _, _, later, stats = build(self.path(), "alpha", 6, consumed)
        self.assertFalse(set(selected_keys(earlier)) & set(selected_keys(later)))
        self.assertFalse(set(consumed) & set(exposure(later)))
        self.assertEqual(stats["familyOutcomes"]["excluded_consumed"], 4)
        self.assertEqual(stats["familyOutcomes"]["eligible"], 10)
        self.assertEqual(stats["families"], 10)
        self.assertEqual(stats["eligiblePool"], 6)
        with self.assertRaises(ValueError):
            build(self.path(), "alpha", 7, consumed)

    def test_exclusion_survives_family_growth_in_newer_backup(self):
        consumed_cases, consumed_labels, earlier, _ = build(
            many_families(6).write(self.path("old.sqlite")), "alpha", 2)
        grown = many_families(6)
        # A newer backup adds an item to one consumed family, changing its familyKey.
        grown.item("ITEM-900", "  " + consumed_cases[0]["request"]["description"].upper(),
                   consumed_labels[0]["maximallySpecificAcceptablePaths"][0]["categoryId"],
                   normalized="NORM-NEW")
        newer = grown.write(self.path("new.sqlite"))
        _, _, full, _ = build(newer, "alpha", 6)
        self.assertEqual(len(set(selected_keys(full)) & set(selected_keys(earlier))), 1)
        self.assertEqual(max(row["itemCount"] for row in full), 2)
        cases, _, later, stats = build(newer, "alpha", 4, exposure(earlier))
        self.assertEqual(stats["familyOutcomes"]["excluded_consumed"], 2)
        self.assertEqual(stats["eligiblePool"], 4)
        consumed_text = {c["request"]["description"].casefold() for c in consumed_cases}
        self.assertFalse(consumed_text & {c["request"]["description"].casefold() for c in cases})

    def test_exclusion_matches_shared_normalized_identity_alone(self):
        old = Backup().item("I-1", "Bartlett pear", "b-fruit", normalized="N-PEAR")
        _, _, earlier, _ = build(old.write(self.path("old.sqlite")), "s", 1)
        newer = (Backup()
                 .item("I-1", "Bartlett pear", "b-fruit", normalized="N-PEAR")
                 .delete("ReceiptItems", "I-1")
                 .item("I-2", "Conference pear", "b-fruit", normalized="n-pear")
                 .item("I-3", "Comice pear", "b-fruit"))
        cases, _, _, stats = build(newer.write(self.path("new.sqlite")), "s", 1, exposure(earlier))
        self.assertEqual(stats["familyOutcomes"], {"eligible": 2, "excluded_consumed": 1})
        self.assertEqual(cases[0]["request"]["description"], "Comice pear")

    def test_deleted_items_and_receipts_are_excluded(self):
        backup = (Backup()
                  .item("I-1", "Kept bolt", "d-bolts", receipt="R-KEEP")
                  .item("I-2", "Deleted bolt", "d-bolts", receipt="R-KEEP")
                  .item("I-3", "Bolt on deleted receipt", "d-bolts", receipt="R-GONE")
                  .item("I-4", "Restored bolt", "d-bolts", receipt="R-KEEP")
                  .delete("ReceiptItems", "i-2")
                  .delete("Receipts", "r-gone")
                  .delete("ReceiptItems", "I-4", deleted_at=None)
                  .delete("Categories", "I-1"))  # other tables are ignored
        cases, _, _, stats = build(backup.write(self.path()), "s", 2)
        self.assertEqual(stats["items"], 2)
        self.assertEqual(sorted(c["request"]["description"] for c in cases),
                         ["Kept bolt", "Restored bolt"])

    def test_invalid_seed_or_size_rejected(self):
        path = many_families(3).write(self.path())
        for seed, size in (("", 1), (None, 1), ("s", 0)):
            with self.subTest(seed=seed, size=size), self.assertRaises(ValueError):
                build(path, seed, size)

    # --- backup safety ----------------------------------------------------------

    def test_backup_is_unchanged_and_opened_read_only(self):
        path = many_families(5).write(self.path())
        before, mtime = sha_file(path), path.stat().st_mtime_ns
        listing = sorted(os.listdir(self.root))
        os.chmod(path, stat.S_IRUSR)
        self.addCleanup(os.chmod, path, stat.S_IRUSR | stat.S_IWUSR)
        build(path, "s", 3)
        self.assertEqual(sha_file(path), before)
        self.assertEqual(path.stat().st_mtime_ns, mtime)
        self.assertEqual(sorted(os.listdir(self.root)), listing)  # no -wal/-journal
        db = backup_cohort._connect(path)
        try:
            with self.assertRaises(sqlite3.OperationalError):
                db.execute("CREATE TABLE intruder (x)")
        finally:
            db.close()

    # --- CLI --------------------------------------------------------------------

    def run_main(self, *argv):
        stdout, stderr = io.StringIO(), io.StringIO()
        code = 0
        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            try:
                main([str(a) for a in argv])
            except SystemExit as exit_:
                code = exit_.code
        return code, stdout.getvalue(), stderr.getvalue()

    def repository(self):
        """Fake checkout with fictional production sources, committed once."""
        repo = self.root / "repo"
        for source in PRODUCTION_SOURCES:
            path = repo / source
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(f"// fictional {Path(source).stem}\nclass Pear {{ }}\n".encode())
        git = ["git", "-C", str(repo)]
        subprocess.run(git[:1] + ["init", "-q", str(repo)], check=True, capture_output=True)
        subprocess.run(git + ["add", "."], check=True, capture_output=True)
        subprocess.run(git + ["-c", "user.name=t", "-c", "user.email=t@example.invalid",
                              "-c", "commit.gpgsign=false", "commit", "-q", "-m", "fixture"],
                       check=True, capture_output=True)
        head = subprocess.run(git + ["rev-parse", "HEAD"], check=True, capture_output=True,
                              text=True).stdout.strip()
        return repo, head

    def cli(self, backup, out, *extra, seed="alpha", size=3, carry=0, repo=None):
        if repo is None:
            repo, _ = self.repository()
        return self.run_main("--backup", backup, "--repository", repo, "--output-dir", out,
                             "--seed", seed, "--size", size,
                             "--prior-carry-nanodollars", carry, *extra)

    def load(self, path):
        return json.loads(path.read_text("utf-8"))

    def test_policy_enforces_size_and_cumulative_ceiling(self):
        for size in (0, 49):
            with self.subTest(size=size), self.assertRaises(ValueError):
                policy_for(size, 0)
        with self.assertRaises(ValueError):
            policy_for(20, 4_900_000_000)
        with self.assertRaises(ValueError):
            policy_for(1, -1)
        policy = policy_for(20, 4_873_863_040)
        self.assertEqual(policy["cases"], 20)
        self.assertEqual(policy["maximumAttempts"], 40)
        self.assertEqual(policy["conservativePriorCarryNanodollars"], 4_873_863_040)
        self.assertLessEqual(4_873_863_040 + 40 * PER_ATTEMPT_NANODOLLARS, CEILING_NANODOLLARS)
        self.assertFalse(policy["liveAuthorized"])
        # Boundary: exactly at the ceiling is allowed, one nanodollar over is not.
        edge = CEILING_NANODOLLARS - 40 * PER_ATTEMPT_NANODOLLARS
        policy_for(20, edge)
        with self.assertRaises(ValueError):
            policy_for(20, edge + 1)
        policy_for(48, 0)

    def test_main_rejects_policy_over_ceiling_without_output(self):
        path = many_families(4).write(self.path())
        out = self.root / "over"
        code, stdout, _ = self.cli(path, out, size=2, carry=CEILING_NANODOLLARS)
        self.assertEqual(code, 2)
        self.assertEqual(stdout, "")
        self.assertFalse(out.exists())

    def test_main_writes_packet_and_separate_labels(self):
        backup = many_families(8)
        path = backup.write(self.path())
        before = sha_file(path)
        repo, head = self.repository()
        out = self.root / "out" / "first"
        code, stdout, stderr = self.cli(path, out, repo=repo, carry=1_000)
        self.assertEqual(code, 0, stderr)
        self.assertEqual(sha_file(path), before)

        packet, label_dir = out / "packet", out / "labels"
        self.assertEqual(sorted(p.name for p in out.iterdir()),
                         ["labels", "manifest.private.json", "membership.private.json", "packet"])
        self.assertEqual(sorted(p.name for p in label_dir.iterdir()),
                         ["labels.private.json", "scoring-freeze.private.json"])
        packet_files = sorted(p.relative_to(packet).as_posix()
                              for p in packet.rglob("*") if p.is_file())
        self.assertEqual(packet_files, sorted(
            ["cases.private.json", "freeze.private.json", "policy.private.json"]
            + ["production-source/" + Path(s).name for s in PRODUCTION_SOURCES]))
        self.assertFalse([p for p in packet.rglob("*") if "label" in p.name])

        freeze = self.load(packet / "freeze.private.json")
        self.assertEqual(freeze["sourceCommit"], head)
        self.assertEqual(freeze["providerCalls"], 0)
        self.assertEqual(set(freeze["filesSha256"]), set(packet_files) - {"freeze.private.json"})
        for name, digest in freeze["filesSha256"].items():
            with self.subTest(name=name):
                self.assertEqual(sha_file(packet / name), digest)
        for source in PRODUCTION_SOURCES:
            self.assertEqual((packet / "production-source" / Path(source).name).read_bytes(),
                             (repo / source).read_bytes())

        policy = self.load(packet / "policy.private.json")
        cases = self.load(packet / "cases.private.json")
        labels = self.load(label_dir / "labels.private.json")
        self.assertEqual(set(labels), {"oracle", "labels"})
        self.assertEqual([l["caseId"] for l in labels["labels"]], [c["id"] for c in cases])
        self.assertNotIn("maximallySpecificAcceptablePaths",
                         (packet / "cases.private.json").read_text("utf-8"))

        scoring = self.load(label_dir / "scoring-freeze.private.json")
        self.assertIs(scoring["beforeCurrentPredictions"], True)
        # Binding names follow aggregate_scoring.py: "membership" pins the cases file.
        self.assertEqual(scoring["sha256"], {
            "membership": sha_file(packet / "cases.private.json"),
            "adjudicatedLabels": sha_file(label_dir / "labels.private.json"),
            "policy": sha_file(packet / "policy.private.json")})
        self.assertEqual(scoring["cohortSize"], 3)
        self.assertEqual(scoring["cohortSize"], policy["cases"])
        self.assertEqual(scoring["cohortSize"], len(cases))
        self.assertEqual(scoring["maximumAttempts"], policy["maximumAttempts"])
        self.assertEqual(scoring["maximumAttempts"], 6)
        self.assertEqual(scoring["priorCarryNanodollars"], 1_000)
        self.assertNotIn("createdAtUtc", scoring)

        manifest = self.load(out / "manifest.private.json")
        self.assertEqual(manifest["backupSha256"], before)
        self.assertEqual(manifest["membershipSha256"], sha_file(out / "membership.private.json"))
        self.assertEqual(manifest["labelsSha256"], sha_file(label_dir / "labels.private.json"))
        self.assertEqual(manifest["packetFilesSha256"], freeze["filesSha256"])
        self.assertEqual(manifest["excludedMemberships"], [])

        stats = json.loads(stdout)
        self.assertEqual(stats, manifest["stats"])

        def leaves(value):
            if isinstance(value, dict):
                for v in value.values():
                    yield from leaves(v)
            else:
                yield value
        self.assertTrue(all(type(v) is int for v in leaves(stats)), stats)
        lowered = stdout.lower()
        forbidden = ([d for _, _, d, _ in backup.items] + [i for i, *_ in backup.items]
                     + [n["CategoryId"] for n in TAXONOMY] + [n["Name"] for n in TAXONOMY]
                     + ["widget", "item-", "case-", "rev-b", "alpha", head])
        for text in forbidden:
            self.assertNotIn(text.lower(), lowered)
        membership = self.load(out / "membership.private.json")
        for row in membership:
            self.assertNotIn(row["familyKey"], stdout)

        # A second run with --exclude never reuses a consumed family.
        second = self.root / "out" / "second"
        code, _, stderr = self.cli(path, second, "--exclude", out / "membership.private.json",
                                   repo=repo, size=5)
        self.assertEqual(code, 0, stderr)
        later = self.load(second / "membership.private.json")
        self.assertFalse(set(selected_keys(membership)) & set(selected_keys(later)))
        self.assertEqual(self.load(second / "manifest.private.json")["excludedMemberships"],
                         [sha_file(out / "membership.private.json")])

    def test_packet_passes_aggregate_scoring_freeze_and_bindings(self):
        path = many_families(8).write(self.path())
        out = self.root / "scored"
        code, _, stderr = self.cli(path, out, size=4, carry=123_456)
        self.assertEqual(code, 0, stderr)
        packet, label_dir = out / "packet", out / "labels"
        freeze = aggregate_scoring.validate_freeze(
            self.load(label_dir / "scoring-freeze.private.json"))
        aggregate_scoring.verify_bindings(freeze, packet / "cases.private.json",
                                          label_dir / "labels.private.json",
                                          packet / "policy.private.json")
        cases = self.load(packet / "cases.private.json")
        labels = self.load(label_dir / "labels.private.json")["labels"]
        for case, label in zip(cases, labels):
            paths = aggregate_scoring._paths(case["request"]["categories"])
            specific, _ = aggregate_scoring._oracle(label, paths)
            self.assertEqual(len(specific), 1)
        # Tampering with any bound file is caught.
        tampered = self.root / "tampered.json"
        tampered.write_bytes((label_dir / "labels.private.json").read_bytes() + b" ")
        with self.assertRaises(ValueError):
            aggregate_scoring.verify_bindings(freeze, packet / "cases.private.json", tampered,
                                              packet / "policy.private.json")

    def test_failed_write_leaves_no_output_or_staging_and_rerun_succeeds(self):
        path = many_families(4).write(self.path())
        repo, _ = self.repository()
        parent = self.root / "parent"
        parent.mkdir()
        out = parent / "cohort"
        real = backup_cohort._write_packet_files

        def explode(stage, *args, **kwargs):
            (stage / "packet").mkdir()
            (stage / "packet" / "cases.private.json").write_text("partial")
            raise OSError("disk full")
        with mock.patch.object(backup_cohort, "_write_packet_files", side_effect=explode):
            code, stdout, _ = self.cli(path, out, size=2, repo=repo)
        self.assertEqual(code, 2)
        self.assertEqual(stdout, "")
        self.assertEqual(list(parent.iterdir()), [])
        self.assertIs(backup_cohort._write_packet_files, real)
        code, _, stderr = self.cli(path, out, size=2, repo=repo)
        self.assertEqual(code, 0, stderr)
        self.assertEqual([p.name for p in parent.iterdir()], ["cohort"])

    def test_main_refuses_dirty_production_sources(self):
        path = many_families(4).write(self.path())
        repo, _ = self.repository()
        (repo / PRODUCTION_SOURCES[0]).write_text("// uncommitted edit\n")
        out = self.root / "dirty"
        code, stdout, _ = self.cli(path, out, size=1, repo=repo)
        self.assertEqual(code, 2)
        self.assertEqual(stdout, "")
        self.assertFalse(out.exists())
        plain = self.root / "plain"
        plain.mkdir()
        code, _, _ = self.cli(path, out, size=1, repo=plain)
        self.assertEqual(code, 2)
        self.assertFalse(out.exists())

    def test_main_refuses_output_inside_git_checkout(self):
        path = many_families(4).write(self.path())
        checkout = self.root / "checkout"
        (checkout / ".git").mkdir(parents=True)
        out = checkout / "nested" / "cohort"
        code, stdout, _ = self.cli(path, out, size=1)
        self.assertEqual(code, 2)
        self.assertEqual(stdout, "")
        self.assertFalse((checkout / "nested").exists())

    def test_main_refuses_existing_output_dir(self):
        path = many_families(4).write(self.path())
        out = self.root / "existing"
        out.mkdir()
        code, stdout, _ = self.cli(path, out, size=1)
        self.assertEqual(code, 2)
        self.assertEqual(stdout, "")
        self.assertEqual(list(out.iterdir()), [])

    def test_main_exits_2_when_size_exceeds_pool(self):
        path = many_families(4).write(self.path())
        out = self.root / "too-many"
        code, stdout, stderr = self.cli(path, out, size=5)
        self.assertEqual(code, 2)
        self.assertEqual(stdout, "")
        self.assertIn("invalid input", stderr)
        self.assertFalse(out.exists())


if __name__ == "__main__":
    unittest.main()
