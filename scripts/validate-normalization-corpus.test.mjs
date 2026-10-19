import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { validateCorpus } from "./validate-normalization-corpus.mjs";

const fixtureUrl = new URL("../docs/normalization-evaluation/corpus.v1.json", import.meta.url);
const load = () => JSON.parse(readFileSync(fixtureUrl, "utf8"));

test("rejects a malformed document with a useful error rather than throwing", () => {
  assert.match(validateCorpus(null).join("\n"), /corpus must be an object/);
});

test("accepts the checked-in synthetic corpus", () => {
  assert.deepEqual(validateCorpus(load()), []);
});

test("rejects a concept family leaking from examples into holdout", () => {
  const corpus = load();
  const example = corpus.cases.find(c => c.split === "example");
  const holdout = corpus.cases.find(c => c.split === "holdout");
  holdout.family = example.family;
  assert.match(validateCorpus(corpus).join("\n"), /family .* crosses splits/);
});

test("detects normalized text leakage despite case, unicode and whitespace changes", () => {
  const corpus = load();
  corpus.cases[0].items[0].description = "Café  Noir";
  corpus.cases.find(c => c.split === "holdout").items[0].description = "  CAFE\u0301\tNOIR ";
  assert.match(validateCorpus(corpus).join("\n"), /description .* crosses splits/);
});

test("allows deliberately duplicated text within one scenario and split", () => {
  const corpus = load();
  corpus.cases[0].items[1].description = corpus.cases[0].items[0].description;
  assert.deepEqual(validateCorpus(corpus), []);
});

test("rejects expected grouping that violates a forbidden merge", () => {
  const corpus = load();
  const scenario = corpus.cases.find(c => c.forbiddenMerges.length);
  const [leftId, rightId] = scenario.forbiddenMerges[0];
  const left = scenario.items.find(i => i.id === leftId);
  const right = scenario.items.find(i => i.id === rightId);
  right.expected = structuredClone(left.expected);
  assert.match(validateCorpus(corpus).join("\n"), /forbidden merge contradicts expected groups/);
});

test("rejects dangling references in forbidden pairs", () => {
  const corpus = load();
  corpus.cases[0].forbiddenMerges.push(["missing", "another-missing"]);
  assert.match(validateCorpus(corpus).join("\n"), /forbidden merge references unknown or identical items/);
});

test("rejects an expectation that resurrects exact rejected text", () => {
  const corpus = load();
  const item = corpus.cases.flatMap(c => c.items).find(i => i.exactRejected);
  item.expected = { action: "group", group: "new-group", display: "Invented group" };
  assert.match(validateCorpus(corpus).join("\n"), /exact rejection requires abstention/);
});

test("rejects reassignment of an existing template-backed item", () => {
  const corpus = load();
  const item = corpus.cases.flatMap(c => c.items).find(i => i.templateGroup !== null);
  item.expected = { action: "group", group: "new-group", display: "Wrong target" };
  assert.match(validateCorpus(corpus).join("\n"), /template-backed item must preserve its current group/);
});

test("rejects attaching to a rejected target even without an exact-source tombstone", () => {
  const corpus = load();
  const scenario = corpus.cases.find(c => c.rejectedGroups.length);
  const item = scenario.items.find(i => !i.exactRejected);
  item.expected = { action: "group", group: scenario.rejectedGroups[0], display: "Rejected target" };
  assert.match(validateCorpus(corpus).join("\n"), /expected group is rejected/);
});

test("rejects inconsistent display labels for the same scenario group", () => {
  const corpus = load();
  corpus.cases[0].items[1].expected.display = "Unrelated display";
  assert.match(validateCorpus(corpus).join("\n"), /group has inconsistent display labels/);
});

test("rejects unknown fields rather than silently admitting unreviewed data", () => {
  const corpus = load();
  corpus.cases[0].items[0].accountName = "Unexpected field";
  assert.match(validateCorpus(corpus).join("\n"), /unknown field accountName/);
});

test("rejects absent immutable financial snapshots and numeric amounts", () => {
  const corpus = load();
  delete corpus.cases[0].items[0].financial;
  corpus.cases[0].items[1].financial.unitPrice = 1.25;
  const errors = validateCorpus(corpus).join("\n");
  assert.match(errors, /missing field financial/);
  assert.match(errors, /unitPrice must be a decimal string/);
});

test("rejects fabricated human approval and missing category coverage", () => {
  const corpus = load();
  corpus.labelStatus = "human-approved";
  for (const scenario of corpus.cases) scenario.tags = scenario.tags.filter(t => t !== "adversarial");
  const errors = validateCorpus(corpus).join("\n");
  assert.match(errors, /labelStatus must be proposed-synthetic/);
  assert.match(errors, /missing required tag adversarial/);
});

for (const [name, mutate, evidence] of [
  ["exact-source tombstone", c => c.cases.flatMap(s => s.items).forEach(i => { i.exactRejected = false; }), "exact rejected source"],
  ["rejected target", c => c.cases.forEach(s => { s.rejectedGroups = []; }), "rejected target"],
  ["template link", c => c.cases.flatMap(s => s.items).forEach(i => {
    if (i.templateGroup !== null) {
      i.templateGroup = null;
      i.currentGroup = null;
      i.expected.action = "group";
    }
  }), "template link"],
  ["forbidden pair", c => c.cases.forEach(s => { s.forbiddenMerges = []; }), "forbidden pair"],
  ["duplicate source rows", c => c.cases.forEach(s => s.items.forEach((i, n) => { i.description += " unique-" + n; })), "duplicate source rows"],
  ["negative financial values", c => c.cases.flatMap(s => s.items).forEach(i => {
    for (const field of ["quantity", "unitPrice", "taxAmount", "totalAmount"]) i.financial[field] = i.financial[field].replace("-", "");
  }), "negative financial value"],
  ["fractional quantity", c => c.cases.flatMap(s => s.items).forEach(i => { i.financial.quantity = i.financial.quantity.startsWith("-") ? "-1.0000" : "1.0000"; }), "fractional quantity"],
]) {
  test("rejects loss of " + name + " even when all category tags remain", () => {
    const corpus = load();
    mutate(corpus);
    const errors = validateCorpus(corpus).join("\n");
    assert.ok(errors.includes("missing structural evidence: " + evidence), errors);
    assert.ok(!errors.includes("missing required tag"), errors);
  });
}
