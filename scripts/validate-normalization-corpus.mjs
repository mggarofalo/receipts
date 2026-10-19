#!/usr/bin/env node
// Offline fixture validation only: this does not run a normalization model.
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const TAGS = ["easy-exact", "abbreviation", "brand", "size-unit", "dietary",
  "merchant-ambiguity", "multilingual", "unicode", "nonproduct", "discount",
  "duplicate", "template", "tombstone", "adversarial", "financial"];
const INVARIANTS = ["preserve-financial-fields", "preserve-source-rows",
  "respect-exact-tombstones", "respect-rejected-targets", "preserve-template-links"];
const nonempty = value => typeof value === "string" && value.trim().length > 0;
const identifier = value => typeof value === "string" && /^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(value);
const normalizedText = value => value.normalize("NFKC").toLowerCase().replace(/\s+/gu, " ").trim();

export function validateCorpus(corpus) {
  const errors = [];
  const fail = (path, message) => errors.push(`${path}: ${message}`);
  const object = (value, path, fields) => {
    if (value === null || typeof value !== "object" || Array.isArray(value)) {
      fail(path, path === "corpus" ? "corpus must be an object" : "must be an object");
      return false;
    }
    for (const field of fields) if (!Object.hasOwn(value, field)) fail(path, `missing field ${field}`);
    for (const field of Object.keys(value)) if (!fields.includes(field)) fail(path, `unknown field ${field}`);
    return true;
  };
  const list = (value, path) => {
    if (!Array.isArray(value)) { fail(path, "must be an array"); return []; }
    return value;
  };
  const oneOf = (value, choices, path) => {
    if (!choices.includes(value)) fail(path, `must be one of ${choices.join(", ")}`);
  };
  const requiredText = (value, path) => { if (!nonempty(value)) fail(path, "must be nonempty text"); };
  const requiredId = (value, path) => { if (!identifier(value)) fail(path, "must be a lowercase identifier"); };
  const nullableId = (value, path) => { if (value !== null) requiredId(value, path); };
  const unique = (values, path) => {
    if (new Set(values).size !== values.length) fail(path, "must not contain duplicates");
  };
  if (!object(corpus, "corpus", ["schemaVersion", "corpusVersion", "created", "provenance",
    "labelStatus", "humanReview", "invariants", "cases"])) return errors;
  if (corpus.schemaVersion !== 1) fail("schemaVersion", "must be 1");
  if (typeof corpus.corpusVersion !== "string" || !/^\d+\.\d+\.\d+$/.test(corpus.corpusVersion)) fail("corpusVersion", "must be a semantic version");
  if (typeof corpus.created !== "string" || !/^\d{4}-\d{2}-\d{2}$/.test(corpus.created)) fail("created", "must be YYYY-MM-DD");
  if (corpus.provenance !== "wholly-synthetic") fail("provenance", "must be wholly-synthetic");
  if (corpus.labelStatus !== "proposed-synthetic") fail("labelStatus", "labelStatus must be proposed-synthetic");
  const invariants = list(corpus.invariants, "invariants");
  if (invariants.length !== INVARIANTS.length || INVARIANTS.some(i => !invariants.includes(i))) fail("invariants", "must contain exactly the required hard invariants");
  unique(invariants, "invariants");
  const scenarios = list(corpus.cases, "cases");
  if (scenarios.length === 0) fail("cases", "must contain scenarios");
  const ids = new Set(), families = new Map(), descriptions = new Map(), tags = new Set(), splits = new Set();
  const evidence = new Set();
  for (const [index, scenario] of scenarios.entries()) {
    const path = `cases[${index}]`;
    if (!object(scenario, path, ["id", "split", "family", "tags", "difficulty", "rationale", "rejectedGroups", "forbiddenMerges", "items"])) continue;
    requiredId(scenario.id, path + ".id");
    if (ids.has(scenario.id)) fail(path, "duplicate case ID");
    ids.add(scenario.id);
    oneOf(scenario.split, ["example", "holdout"], path + ".split");
    splits.add(scenario.split);
    requiredId(scenario.family, path + ".family");
    if (families.has(scenario.family) && families.get(scenario.family) !== scenario.split) fail(path, `family ${scenario.family} crosses splits`);
    families.set(scenario.family, scenario.split);
    oneOf(scenario.difficulty, ["exact", "fuzzy", "ambiguous", "protected", "adversarial"], path + ".difficulty");
    requiredText(scenario.rationale, path + ".rationale");
    const caseTags = list(scenario.tags, path + ".tags");
    if (caseTags.length === 0) fail(path, "must have category tags");
    unique(caseTags, path + ".tags");
    for (const tag of caseTags) { oneOf(tag, TAGS, path + ".tags"); tags.add(tag); }
    const rejected = list(scenario.rejectedGroups, path + ".rejectedGroups");
    if (rejected.length) evidence.add("rejected target");
    unique(rejected, path + ".rejectedGroups");
    for (const group of rejected) requiredId(group, path + ".rejectedGroups");
    const items = list(scenario.items, path + ".items"), itemMap = new Map(), labels = new Map(), caseDescriptions = new Set();
    if (items.length < 2) fail(path, "must contain at least two items");
    for (const [itemIndex, item] of items.entries()) {
      const ip = `${path}.items[${itemIndex}]`;
      if (!object(item, ip, ["id", "description", "merchant", "currentGroup", "templateGroup", "exactRejected", "financial", "expected"])) continue;
      requiredId(item.id, ip + ".id");
      if (itemMap.has(item.id)) fail(ip, "duplicate item ID");
      itemMap.set(item.id, item);
      requiredText(item.description, ip + ".description");
      if (caseDescriptions.has(item.description)) evidence.add("duplicate source rows");
      caseDescriptions.add(item.description);
      if (nonempty(item.description)) {
        const key = normalizedText(item.description);
        if (descriptions.has(key) && descriptions.get(key) !== scenario.split) fail(ip, `description ${key} crosses splits`);
        descriptions.set(key, scenario.split);
      }
      requiredText(item.merchant, ip + ".merchant");
      nullableId(item.currentGroup, ip + ".currentGroup");
      nullableId(item.templateGroup, ip + ".templateGroup");
      if (typeof item.exactRejected !== "boolean") fail(ip, "exactRejected must be boolean");
      if (item.exactRejected === true) evidence.add("exact rejected source");
      if (identifier(item.templateGroup)) evidence.add("template link");
      if (object(item.financial, ip + ".financial", ["quantity", "unitPrice", "taxAmount", "currency", "totalAmount"])) {
        for (const field of ["quantity", "unitPrice", "taxAmount", "totalAmount"]) {
          if (typeof item.financial[field] !== "string" || !/^-?\d+\.\d{1,4}$/.test(item.financial[field])) fail(ip, `${field} must be a decimal string with up to four decimal places`);
          if (Number(item.financial[field]) < 0) evidence.add("negative financial value");
        }
        if (Number.isFinite(Number(item.financial.quantity)) && !Number.isInteger(Number(item.financial.quantity))) evidence.add("fractional quantity");
        if (item.financial.currency !== "USD") fail(ip, "synthetic currency must be USD");
      }
      if (!object(item.expected, ip + ".expected", ["action", "group", "display"])) continue;
      const expected = item.expected;
      oneOf(expected.action, ["group", "abstain", "preserve"], ip + ".expected.action");
      if (expected.action === "abstain") {
        if (expected.group !== null || expected.display !== null) fail(ip, "abstention requires null group and display");
      } else {
        requiredId(expected.group, ip + ".expected.group");
        requiredText(expected.display, ip + ".expected.display");
        if (labels.has(expected.group) && labels.get(expected.group) !== expected.display) fail(ip, "group has inconsistent display labels");
        labels.set(expected.group, expected.display);
      }
      if (item.exactRejected && expected.action !== "abstain") fail(ip, "exact rejection requires abstention");
      if (expected.group !== null && rejected.includes(expected.group)) fail(ip, "expected group is rejected");
      if (item.templateGroup !== null && (item.currentGroup !== item.templateGroup || expected.action !== "preserve" || expected.group !== item.templateGroup)) fail(ip, "template-backed item must preserve its current group");
      if (expected.action === "preserve" && (item.currentGroup === null || expected.group !== item.currentGroup)) fail(ip, "preserve requires the existing current group");
      if (item.currentGroup !== null && expected.action !== "preserve") fail(ip, "already linked item is not an unresolved candidate");
    }
    const pairs = list(scenario.forbiddenMerges, path + ".forbiddenMerges");
    const pairKeys = [];
    for (const pair of pairs) {
      if (!Array.isArray(pair) || pair.length !== 2 || pair[0] === pair[1] || !pair.every(id => itemMap.has(id))) {
        fail(path, "forbidden merge references unknown or identical items"); continue;
      }
      pairKeys.push([...pair].sort().join("/"));
      evidence.add("forbidden pair");
      const left = itemMap.get(pair[0]).expected, right = itemMap.get(pair[1]).expected;
      if (left?.group != null && left.group === right?.group) fail(path, "forbidden merge contradicts expected groups");
    }
    unique(pairKeys, path + ".forbiddenMerges");
  }
  for (const tag of TAGS) if (!tags.has(tag)) fail("cases", `missing required tag ${tag}`);
  for (const required of ["exact rejected source", "rejected target", "template link",
    "forbidden pair", "duplicate source rows", "negative financial value", "fractional quantity"]) {
    if (!evidence.has(required)) fail("cases", `missing structural evidence: ${required}`);
  }
  for (const split of ["example", "holdout"]) if (!splits.has(split)) fail("cases", `missing split ${split}`);
  if (object(corpus.humanReview, "humanReview", ["status", "reviews", "disagreements"])) {
    if (corpus.humanReview.status !== "unadjudicated") fail("humanReview", "v1 status must remain unadjudicated until an explicitly reviewed revision");
    // This initial corpus has no human review. Agent review is not human adjudication.
    for (const field of ["reviews", "disagreements"]) {
      if (list(corpus.humanReview[field], "humanReview." + field).length !== 0) fail("humanReview", "v1 registry is empty; record actual review in a new corpus/schema revision");
    }
  }
  return errors;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const path = process.argv[2] ?? fileURLToPath(new URL("../docs/normalization-evaluation/corpus.v1.json", import.meta.url));
    const corpus = JSON.parse(readFileSync(path, "utf8"));
    const errors = validateCorpus(corpus);
    if (errors.length) {
      console.error(errors.join("\n"));
      process.exitCode = 1;
    } else {
      console.log(JSON.stringify({
        corpusVersion: corpus.corpusVersion,
        cases: corpus.cases.length,
        items: corpus.cases.reduce((count, scenario) => count + scenario.items.length, 0),
        examples: corpus.cases.filter(scenario => scenario.split === "example").length,
        holdout: corpus.cases.filter(scenario => scenario.split === "holdout").length,
        status: "fixtures-valid; labels-unadjudicated; no-model-run",
      }, null, 2));
    }
  } catch (error) {
    console.error(`Unable to validate corpus: ${error.message}`);
    process.exitCode = 1;
  }
}
