import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import sqlite3
import stat
import subprocess
import tempfile
import unittest

import round4_cohort
from round4_cohort import allocate, build, gold_for, load_roles, main, policy_for

REVISION = "rev-s"


def node(category, parent=None, name=None):
    return {"RevisionId": REVISION, "CategoryId": category, "ParentId": parent,
            "Name": name or category.title(), "Definition": "", "Archived": False}


# Fictional depth-2 tree: three item roots, one person-item root, two envelope roots.
TAXONOMY = [
    node("food"), node("fruit", "food"), node("bread", "food"), node("food-na", "food", "N/A"),
    node("tools"), node("bolts", "tools"),
    node("toys"), node("blocks", "toys"),
    node("kid-food", name="Kid Food"), node("kid-snacks", "kid-food", "Snacks"),
    node("party"), node("party-misc", "party", "Misc"),
    node("outing"), node("outing-misc", "outing", "Misc"),
]
ROLES = {"roots": {"Food": {"role": "item"}, "Tools": {"role": "item"}, "Toys": {"role": "item"},
                   "Kid Food": {"role": "person-item", "itemRoot": "Food"},
                   "Party": {"role": "envelope"}, "Outing": {"role": "envelope"}},
         "notes": {"ignored": "extra top-level keys are allowed"}}


class SyntheticBackup:
    def __init__(self):
        self.items, self.assignments, self.current, self.receipts = [], [], [], {}

    def item(self, item_id, description, category, receipt="r-1", store="Corner Shop"):
        self.receipts[receipt] = store
        self.items.append((item_id, receipt, description, None))
        self.assignments.append({"Id": "a-" + item_id, "CategoryId": category, "Source": "legacy_import"})
        self.current.append({"ReceiptItemId": item_id, "AssignmentId": "a-" + item_id})
        return self

    def write(self, path):
        db = sqlite3.connect(path)
        try:
            db.execute("CREATE TABLE receipts (id TEXT PRIMARY KEY, location TEXT NOT NULL)")
            db.execute("CREATE TABLE receipt_items (id TEXT PRIMARY KEY, receipt_id TEXT, "
                       "description TEXT, normalized_description_id TEXT)")
            db.execute("CREATE TABLE taxonomy_archive (kind TEXT, id TEXT, payload TEXT)")
            db.execute("CREATE TABLE portable_deletion_state (kind TEXT, id TEXT, payload TEXT)")
            db.executemany("INSERT INTO receipts VALUES (?, ?)", self.receipts.items())
            db.executemany("INSERT INTO receipt_items VALUES (?, ?, ?, ?)", self.items)
            rows = ([("head", {"RevisionId": REVISION})] + [("node", n) for n in TAXONOMY]
                    + [("assignment", a) for a in self.assignments]
                    + [("current", c) for c in self.current])
            db.executemany("INSERT INTO taxonomy_archive VALUES (?, ?, ?)",
                           [(k, str(i), json.dumps(v)) for i, (k, v) in enumerate(rows)])
            db.commit()
        finally:
            db.close()
        return path


def standard_backup():
    """Many fruit families, a few of each other kind, distinct texts so each item is a family."""
    backup = SyntheticBackup()
    for i in range(30):
        backup.item(f"f{i}", f"synthetic pear {i}", "fruit", receipt=f"r-f{i}")
    for i in range(6):
        backup.item(f"b{i}", f"synthetic loaf {i}", "bread")
        backup.item(f"n{i}", f"synthetic legacy grocery {i}", "food-na")
        backup.item(f"t{i}", f"synthetic bolt {i}", "bolts")
        backup.item(f"y{i}", f"synthetic block {i}", "blocks")
        backup.item(f"k{i}", f"synthetic kid snack {i}", "kid-snacks")
        backup.item(f"p{i}", f"synthetic party plate {i}", "party-misc")
        backup.item(f"v{i}", f"synthetic outing snack {i}", "outing-misc")
    return backup


class Round4CohortTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.roles = self.root / "roles.json"
        self.roles.write_text(json.dumps(ROLES), encoding="utf-8")
        self.backup = standard_backup().write(self.root / "backup.sqlite")

    def tearDown(self):
        self.temp.cleanup()

    def build(self, **overrides):
        args = {"placeholders": ["N/A"], "seed": "alpha", "size": 14, "envelope_cases": 2,
                "probes": 2, "root_cap": 4, "exclude": ()}
        args.update(overrides)
        return build(self.backup, self.roles, **args)

    def test_roles_must_cover_exactly_the_taxonomy_roots(self):
        names = list(ROLES["roots"])
        self.assertEqual(set(load_roles(self.roles, names)), set(names))
        for broken in ({k: v for k, v in ROLES["roots"].items() if k != "Outing"},
                       {**ROLES["roots"], "Food": {"role": "budget"}},
                       {**ROLES["roots"], "Kid Food": {"role": "person-item", "itemRoot": "Party"}},
                       {**ROLES["roots"], "Toys": {"role": "item", "itemRoot": "Food"}}):
            self.roles.write_text(json.dumps({"roots": broken}), encoding="utf-8")
            with self.assertRaises(ValueError):
                load_roles(self.roles, names)

    def test_gold_targets_by_role_and_placeholder(self):
        categories = [{"id": n["CategoryId"], "parentId": n["ParentId"]} for n in TAXONOMY]
        paths = round4_cohort.bc.paths_for(categories)
        names = {n["CategoryId"]: n["Name"] for n in TAXONOMY}
        roles = ROLES["roots"]
        self.assertEqual(gold_for("fruit", paths, names, roles, {"n/a"}),
                         {"stratum": "item", "scoredVia": "item", "goldRootId": "food",
                          "goldLeafId": "fruit", "leafScored": True, "placeholderGold": False})
        placeholder = gold_for("food-na", paths, names, roles, {"n/a"})
        self.assertEqual((placeholder["goldLeafId"], placeholder["leafScored"], placeholder["placeholderGold"]),
                         (None, False, True))
        person = gold_for("kid-snacks", paths, names, roles, {"n/a"})
        self.assertEqual((person["stratum"], person["goldRootId"], person["leafScored"]),
                         ("item", "food", False))
        self.assertEqual(gold_for("party-misc", paths, names, roles, {"n/a"})["stratum"], "envelope")

    def test_allocation_gives_breadth_then_sqrt_weight_under_a_cap(self):
        pools = {"big": [0] * 100, "mid": [0] * 9, "one": [0], "two": [0] * 2}
        seats = allocate(pools, 10, "alpha", cap=5, start_one=True)
        self.assertEqual(sum(seats.values()), 10)
        self.assertTrue(all(seats[r] >= 1 for r in pools))
        self.assertEqual(seats["big"], 5)  # capped
        self.assertLessEqual(seats["one"], 1)
        self.assertEqual(seats, allocate(dict(reversed(list(pools.items()))), 10, "alpha", cap=5, start_one=True))
        envelope = allocate(pools, 2, "alpha", cap=1, start_one=False)
        self.assertEqual(sum(envelope.values()), 2)
        with self.assertRaises(ValueError):
            allocate({"one": [0]}, 2, "alpha", cap=5, start_one=True)

    def test_build_strata_taxonomy_stores_and_probes(self):
        cases, taxonomy, labels, membership, stats = self.build()
        by_id = {c["id"]: c for c in cases}
        gold = {l["caseId"]: l for l in labels["labels"]}
        self.assertEqual(len(cases), 14)
        self.assertEqual(sum(l["stratum"] == "envelope" for l in gold.values()), 2)
        self.assertEqual(len(labels["probes"]), 2)
        # Only item roots are offered; placeholder leaves are dropped.
        self.assertEqual([r["name"] for r in taxonomy["roots"]], ["Food", "Tools", "Toys"])
        self.assertEqual([l["name"] for l in taxonomy["roots"][0]["leaves"]], ["Bread", "Fruit"])
        self.assertLessEqual(stats["maximumCasesPerRoot"], 4)
        self.assertEqual(stats["itemRootsSampled"], 3)
        self.assertTrue(all(c["store"] == "Corner Shop" for c in cases))
        self.assertTrue(all(set(c) == {"id", "description", "store"} for c in cases))
        for probe in labels["probes"]:
            clean = gold[probe["cleanCaseId"]]
            self.assertEqual(clean["stratum"], "item")
            self.assertNotEqual(probe["injectedRootId"], clean["goldRootId"])
            target = next(r["name"] for r in taxonomy["roots"] if r["id"] == probe["injectedRootId"])
            text = by_id[probe["caseId"]]["description"]
            self.assertTrue(text.startswith(by_id[probe["cleanCaseId"]]["description"]))
            self.assertIn(target, text)
        self.assertEqual(len(membership), 12)
        self.assertEqual(self.build(), (cases, taxonomy, labels, membership, stats))
        self.assertNotEqual([c["id"] for c in self.build(seed="beta")[0]], [c["id"] for c in cases])

    def test_store_is_most_frequent_location_and_blank_is_omitted(self):
        backup = standard_backup()
        backup.item("s1", "synthetic shared", "bolts", receipt="r-s1", store="Big Store")
        backup.item("s2", "synthetic shared", "bolts", receipt="r-s2", store="Big Store")
        backup.item("s3", "synthetic shared", "bolts", receipt="r-s3", store="Small Store")
        backup.item("s4", "synthetic blank", "blocks", receipt="r-s4", store="   ")
        path = backup.write(self.root / "stores.sqlite")
        cases, _, labels, _, _ = build(path, self.roles, ["N/A"], "alpha", 46, 0, 0, 40)
        stores = {c["description"]: c["store"] for c in cases}
        self.assertEqual(stores["synthetic shared"], "Big Store")
        self.assertIsNone(stores["synthetic blank"])

    def test_exclusion_removes_consumed_families(self):
        _, _, _, membership, _ = self.build()
        consumed = [k for row in membership for k in row["exposureKeys"]]
        _, _, _, again, stats = self.build(exclude=consumed)
        self.assertFalse({r["familyKey"] for r in membership} & {r["familyKey"] for r in again})
        self.assertEqual(stats["familyOutcomes"]["excluded_consumed"], 12)

    def test_policy_carries_owner_inputs_and_refuses_overruns(self):
        policy = policy_for(60, 250_000_000, 5_000_000_000, 120, 350_000_000)
        self.assertEqual((policy["maximumAttempts"], policy["conservativePriorCarryNanodollars"],
                          policy["cumulativeCeilingNanodollars"], policy["liveAuthorized"]),
                         (120, 250_000_000, 5_000_000_000, False))
        for args in ((61, 250_000_000, 5_000_000_000, 120, 350_000_000),
                     (60, 4_800_000_000, 5_000_000_000, 120, 350_000_000),
                     (60, -1, 5_000_000_000, 120, 350_000_000)):
            with self.assertRaises(ValueError):
                policy_for(*args)

    def repository(self):
        repo = self.root / "repo"
        if repo.exists():
            return repo
        repo.mkdir()
        subprocess.run(["git", "init", "-q", str(repo)], check=True)
        (repo / "x").write_text("synthetic", encoding="utf-8")
        subprocess.run(["git", "-C", str(repo), "add", "x"], check=True)
        subprocess.run(["git", "-C", str(repo), "-c", "user.name=t", "-c", "user.email=t@example.invalid",
                        "commit", "-q", "--no-verify", "-m", "synthetic"], check=True)
        return repo

    def cli(self, out, *extra):
        argv = ["--backup", str(self.backup), "--root-roles", str(self.roles), "--placeholder-leaf", "N/A",
                "--repository", str(self.repository()), "--output-dir", str(out), "--seed", "alpha",
                "--size", "14", "--envelope-cases", "2", "--probes", "2", "--root-cap", "4",
                "--prior-carry-nanodollars", "250000000", "--ceiling-nanodollars", "5000000000",
                "--approved-maximum-attempts", "120",
                "--approved-reservation-cap-nanodollars", "350000000", *extra]
        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            main(argv)
        return stdout.getvalue()

    def test_main_writes_private_packet_and_separate_labels(self):
        out = self.root / "private" / "v4"
        printed = self.cli(out)
        self.assertNotIn("synthetic", printed)
        self.assertNotIn("Corner Shop", printed)
        packet = out / "packet"
        freeze = json.loads((packet / "freeze.private.json").read_text("utf-8"))
        self.assertEqual(set(freeze["filesSha256"]),
                         {"cases.private.json", "taxonomy.private.json", "policy.private.json"})
        for name, digest in freeze["filesSha256"].items():
            self.assertEqual(hashlib.sha256((packet / name).read_bytes()).hexdigest(), digest)
        self.assertNotIn("goldRootId", (packet / "cases.private.json").read_text("utf-8"))
        scoring = json.loads((out / "labels" / "scoring-freeze.private.json").read_text("utf-8"))
        self.assertEqual(scoring["sha256"]["labels"],
                         hashlib.sha256((out / "labels" / "labels.private.json").read_bytes()).hexdigest())
        self.assertEqual((scoring["cohortSize"], scoring["probeCount"]), (14, 2))
        for path in out.rglob("*"):
            mode = stat.S_IMODE(path.stat().st_mode)
            self.assertEqual(mode, 0o700 if path.is_dir() else 0o600, path.name)
        self.assertEqual(stat.S_IMODE(out.stat().st_mode), 0o700)

    def test_main_refuses_existing_output_and_overrun_without_writing(self):
        out = self.root / "private" / "v4"
        self.cli(out)
        with self.assertRaises(SystemExit) as raised, contextlib.redirect_stderr(io.StringIO()):
            self.cli(out)
        self.assertEqual(raised.exception.code, 2)
        other = self.root / "private" / "over"
        with self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
            self.cli(other, "--approved-maximum-attempts", "20")
        self.assertFalse(other.exists())
        self.assertEqual(sorted(p.name for p in (self.root / "private").iterdir()), ["v4"])


if __name__ == "__main__":
    unittest.main()
