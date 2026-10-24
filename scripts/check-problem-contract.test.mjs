import assert from "node:assert/strict";
import test from "node:test";
import { compareProblemContracts } from "./check-problem-contract.mjs";

const problem = "application/problem+json";
const schema = { $ref: "#/components/schemas/ProblemDetails" };
const response = (media = problem, bodySchema = schema) => ({ content: { [media]: { schema: bodySchema } } });
const document = (responses) => ({ paths: { "/api/items/{id}": { get: { responses } } } });

test("checks each declared problem status and accepts unrelated JSON and bodiless responses", () => {
  const spec = document({ 400: response(), 404: {}, 409: response("application/json", { $ref: "#/components/schemas/MergeConflict" }), 503: response() });
  assert.deepEqual(compareProblemContracts(spec, structuredClone(spec)), { checked: 2, errors: [] });
});

test("rejects a missing problem status independently of other statuses", () => {
  const result = compareProblemContracts(document({ 400: response(), 409: response() }), document({ 400: response() }));
  assert.equal(result.checked, 2);
  assert.match(result.errors.join("\n"), /GET .*409.*generated/);
});

test("rejects ordinary JSON even if both documents regress together", () => {
  const broken = document({ 400: response("application/json") });
  const result = compareProblemContracts(broken, structuredClone(broken));
  assert.equal(result.checked, 1);
  assert.match(result.errors.join("\n"), /canonical.*application\/problem\+json/);
  assert.match(result.errors.join("\n"), /generated.*application\/problem\+json/);
});

test("rejects extra ordinary JSON media and an incorrect problem schema", () => {
  const generated = document({ 400: response(), 409: response(problem, { type: "string" }) });
  generated.paths["/api/items/{id}"].get.responses[400].content["application/json"] = { schema };
  const result = compareProblemContracts(document({ 400: response(), 409: response() }), generated);
  assert.equal(result.errors.length, 2);
  assert.match(result.errors[0], /400.*generated.*media/);
  assert.match(result.errors[1], /409.*generated.*ProblemDetails/);
});

test("matches generated route casing and parameter names like the general drift check", () => {
  const generated = { paths: { "/api/Items/{itemId}": { get: { responses: { 400: response() } } } } };
  assert.deepEqual(compareProblemContracts(document({ 400: response() }), generated), { checked: 1, errors: [] });
});
