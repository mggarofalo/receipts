# Synthetic normalization evaluation corpus

RECEIPTS-1013, initial revision 2026-10-19. This directory contains fixtures and
an evaluation rubric, not a provider integration or evidence of model quality.

[corpus.v1.json](corpus.v1.json) contains 32 scenarios and 65 item rows: eight
example scenarios and 24 holdout scenarios. All descriptions, merchant identifiers,
brands and amounts were invented for this fixture. No receipt exports, account
names, provider credentials or real financial details were used.

Labels and scenarios are agent-authored **proposed synthetic assumptions**, written
before any model run.
Independent agent review can find contradictions; it does not make these labels
human-adjudicated ground truth. The product owner still needs to decide brand,
package-size and variant aggregation policy, display-language preferences,
minimum suggestion precision and acceptable abstention before live experiments.
There are no quality scores or provider runs to report.

## Run the offline checks

From the repository root, with Node 24:

```sh
node scripts/validate-normalization-corpus.mjs
node --test scripts/validate-normalization-corpus.test.mjs
```

The validator accepts an optional corpus path. It reads local JSON only, needs no
npm packages, network, database, ONNX model or provider credentials, and exits
nonzero for invalid fixtures. Success means the fixture structure and its declared
constraints are internally consistent. It does not prove semantic labels,
application invariants or model behavior.

Checks cover required/unknown fields, identifiers, decimal strings, category
coverage, duplicate references, consistent display labels, explicit forbidden
pairs, existing template links, rejected targets and exact-source tombstones.
Required structural evidence cannot be satisfied by tags alone: the corpus must
retain actual tombstones, rejected targets, template links, forbidden pairs,
duplicate source rows, negative financial values and fractional quantities.
Example/holdout separation is checked by concept-family identifier and description
after NFKC normalization, lowercasing and whitespace folding. That normalization
exists only for leakage detection; it does not change production matching policy.
A reviewer must still catch paraphrases or related families that evade those checks.

## Fixture contract

`schemaVersion` versions the fixture shape; `corpusVersion` versions its content.
Case IDs are stable. Item and group IDs are local to one case and are never
production database IDs. A case is an isolated synthetic registry snapshot; there
is no grouping across cases. Do not send expected labels or fixture IDs that encode
answers to a provider.

| Field | Meaning |
| --- | --- |
| `split` | `example` may inform prompt development; `holdout` must not enter prompts, few-shot examples or tuning. |
| `family` | Concept-family leakage boundary. Related variants belong in one split even when their text differs. |
| `difficulty`, `tags` | Reporting strata; tags may overlap, so tag totals are not additive. |
| `rationale` | Proposed interpretation and uncertainty, not a model-generated explanation. |
| `rejectedGroups` | Synthetic unavailable registry targets. No suggestion may attach to them. |
| `forbiddenMerges` | Item-ID pairs that must not receive the same non-null group under this revision's proposed policy. Two abstentions are not a merge. |
| `merchant` | Fictional context only. Merchant identity must not invent missing product details. |
| `currentGroup`, `templateGroup` | Existing assignment/provenance, or null. An already template-stamped row is not an unresolved inference candidate. |
| `exactRejected` | An exact source-text tombstone exists; choosing a different target cannot bypass it. |
| `financial` | Immutable synthetic purchase fields, stored as decimal strings to avoid binary rounding. Keep these local; they are not provider input. |
| `expected.action` | `group` proposes a semantic group; `abstain` leaves group/display null; `preserve` keeps an existing assignment. |
| `expected.group`, `expected.display` | Proposed equivalence class and useful display exemplar. Display wording is not a calibrated truth or the canonical matching key. |

The version-1 validator deliberately accepts only this initial unadjudicated
snapshot. Human review must produce a new corpus revision and update the review
record contract and validator together; do not change a status string to imply
approval without the review evidence.

## Semantic labels versus hard invariants

Different fictional brands, package sizes and dietary variants are kept separate
as a conservative **proposal**. Equivalent explicit units and some abbreviations
are grouped. Ambiguous names and missing sizes abstain. These choices need human
adjudication; a future owner decision may legitimately change them in a new
version. Nonproduct and discount abstention is also an evaluation proposal, not a
claim that the existing resolver already behaves that way.

The following hard boundaries come from existing application ownership, not these
semantic proposals:

- Never edit quantity, unit price, tax, persisted total amount, currency or source description, delete
  purchase rows, or consolidate repeated purchases as part of normalization.
  Compare the entire before/after source-row multiset and every persisted monetary
  field, including the supplied totalAmount snapshot. Do not recompute a total
  from quantity × unit price and treat that as proof the stored value survived.
- Never attach an exact rejected source or attach to a rejected target.
- Preserve existing template-stamped assignments in the unresolved-item
  suggestion path. Do not fabricate a similarity score for template provenance.
- Run inference outside database write locks. Final apply must revalidate live
  source/target state and commit accepted changes and audit together.

See [normalization ownership](../normalization-ownership.md),
[item vocabulary precedence](../design/item-vocabularies.md), and
[receipt arithmetic](../receipt-arithmetic.md). Templates do not prevent later
explicit admin merges/rejections; deliberate template declarations can reinstate
tombstones through their existing separate path. Those actions are not model
suggestions and are outside these static cases. Stale writes, transaction ordering,
audit atomicity and crash recovery require implementation/integration tests; this
corpus does not establish them.

Zero tombstone, rejected-target, template and financial/source-row violations is
mandatory for any candidate release. Keep semantic forbidden-pair violations
separate from those hard violations so a disputed label does not masquerade as an
existing application guarantee.

## Coverage and split discipline

| Category | Representative scenario IDs |
| --- | --- |
| Exact matches and duplicate purchase rows | ex-pasta, ho-lentils, ho-apples-duplicate |
| Abbreviations and insufficient expansions | ex-oat, ho-cheddar, ho-abbreviation-unknown |
| Fictional brands, sizes, units, multipacks | ex-rice-brand, ex-water, ho-soap-brands, ho-juice-multipack, ho-flour-units |
| Dietary variants | ex-bread-diet, ho-coffee-diet, ho-milk-diet, ho-sugar-variant |
| Merchant ambiguity | ex-house-special, ho-merchant |
| Multilingual and Unicode | ho-bilingual, ho-unicode, ho-fullwidth |
| Nonproducts, discounts, deposits | ex-tax, ho-discount, ho-deposit |
| Curated template concepts and tombstones | ex-template-beans, ho-template-tea, ho-exact-tombstone, ho-rejected-target |
| Adversarial instructions | ho-injection |
| Signed and fractional financial preservation | ho-discount, ho-weighted, ho-refund |

Freeze the holdout before prompt development. A reviewer may inspect it to audit
labels, but prompt authors must not use it to tune a prompt or choose examples.
If outputs lead to a prompt change, treat the inspected cases as development data
and add a new untouched holdout before making a generalization claim. This small,
public synthetic corpus is a regression aid; it cannot be a permanently secret
benchmark or support statistical claims about real receipts.

## Future comparison procedure

RECEIPTS-1014 owns the evaluator and provider capability experiment. The following
is the **intended command contract**, not an executable command supplied by this PR:

```sh
node scripts/compare-normalization.mjs --corpus docs/normalization-evaluation/corpus.v1.json --split holdout --baseline baseline-results.json --candidate candidate-results.json --report comparison.json
```

The future comparator reads already captured local results and must not contact a
provider. Baseline/candidate capture are separate authorized tasks. Require exactly
one result per case/item, reject unknown or duplicate IDs, and report missing,
invalid or failed results explicitly. A failed response is not an abstention.
Do not silently exclude failures from denominators.

Capture the existing deterministic/embedding resolver on isolated identical
synthetic registry snapshots, using the same fixed item order and initial state
for each arm. Record repository commit, policy thresholds, pinned ONNX artifact
hash/version and model availability; unavailable embeddings and their existing
fallback must be reported separately. Do not substitute string equality or these
expected labels for the real baseline. Compare baseline and candidate against the
same proposed labels rather than calling the baseline ground truth. Existing
exact/tombstone/template precedence remains in force.

Strip expected fields, rationales, split labels and financial snapshots from any
future provider payload; map opaque request IDs locally. Provide only the approved
minimal item and registry facts. Keep model output untrusted. Before a provider
experiment, obtain the deployment/consent and product-policy decisions tracked by
RECEIPTS-1000/1001/1002/1014. Fixture validation authorizes no inference or usage.

## Metrics and denominators

Report raw numerators and denominators alongside rates. Use `null` with a reason
for a zero denominator, never 100% or 0% by convention. Report holdout separately
from examples, then exact/fuzzy/ambiguous/protected/adversarial strata and each
tag. Report sample counts; 24 holdout scenarios are not population estimates.

Let U be all unlinked, non-exact-rejected input rows in the selected split, including
ambiguous/nonproduct rows. Let S be the U rows with a valid non-abstaining suggestion,
A the U rows with explicit abstention, and F the U rows with missing, failed or
invalid output. S + A + F must equal |U|. Existing linked rows and exact-source
tombstones are reported in the hard-invariant lane, not padded into suggestion
precision.

For grouping, consider all unordered pairs within a case whose two expected
actions are `group`. Expected group equality defines positive pairs; unequal
groups define negative pairs. Predicted equality means both valid outputs have the
same non-null predicted group, independent of the chosen ID spelling. Abstention
or failure never predicts equality. Protected rows and expected abstentions are
outside this pair denominator and have separate safety metrics.

| Metric | Exact definition |
| --- | --- |
| Grouping precision | Correct predicted-equal pairs / all predicted-equal pairs in the pair universe. Singleton suggestions create no pair credit. |
| False-merge rate | Predicted-equal expected-negative pairs / all expected-negative pairs, not all pairs or all suggestions. |
| Grouping recall | Correct predicted-equal pairs / all expected-positive pairs. Prevents abstention or singleton splitting from appearing perfect. |
| Suggestion precision | Human-judged semantically correct suggestions among S / |S|, after blind adjudication; includes correct target, retained distinguishing attributes and no unsupported inference. Unadjudicated results remain null. |
| Abstention / coverage / failure | |A| / |U|, |S| / |U|, and |F| / |U| respectively. Coverage is not accuracy. |
| Unsupported-guess rate | Non-abstaining suggestions on expected-abstain rows in U / all expected-abstain rows in U. U excludes exact-source tombstones, whose violations are reported separately. |
| Forbidden-pair rate | Explicit forbidden pairs assigned the same non-null group / all declared forbidden pairs. |
| Label usefulness | Human-rated useful displays / all display-bearing suggestions. Rating requires concise, recognizable wording, preserved distinguishing facts, no invention and accepted language; unresolved ratings stay visible. Exact exemplar string equality is only a diagnostic. |
| Hard violations | Counts for rejected-source/target attachment, template reassignment, source-row mutation and financial mutation; all must be zero, including on failed or abstained rows. |
| Regression | Per-case before/after outcome table plus numerator/denominator and rate differences for each metric on identical eligible populations; list every newly violated hard rule and newly wrong/abstained/failed case. |

An optional local financial impact diagnostic may sum absolute preserved synthetic
totalAmount snapshots affected by false merges using decimal arithmetic, once
per affected source row, and divide by absolute amounts of all eligible source
rows. Do not recompute stored totals from quantity and price. State any display
rounding rule and avoid double-counting an item with several
wrong pairings. This is not real money, model calibration or a substitute for
semantic precision. Do not upload those financial snapshots.

## Review registry and release gates

The corpus records `humanReview.status: unadjudicated` with empty `reviews` and
`disagreements`. Empty means **no human review recorded**, not human consensus.
When review occurs, preserve the old revision and record reviewer identity/date,
case IDs reviewed, each competing interpretation, rationale, unresolved questions,
and the product owner's resolution in a new revision. Review labels without
seeing candidate outputs first. Keep unresolved cases out of any claim of
adjudicated suggestion precision, but continue reporting their outcomes and counts.

Before a live comparison, the owner must record numeric minimum suggestion
precision, maximum acceptable abstention/failure, treatment of uncertain labels,
and required minimum sample sizes. Their present values are **undecided**.
Do not derive thresholds from whatever this candidate happens to score.
Hard-invariant tolerance remains zero. Confidence prose is not a probability;
schema validity is not correctness. No model selection or auto-apply decision can
be justified by these fixtures alone.

The future report must contain: corpus version/hash; code and baseline/model
versions; prompt/schema versions; selected split and case IDs; product decisions
and review status; per-item outcome (`suggest`, `abstain`, `failed`, `preserve`);
per-case pair/forbidden-pair counts; every metric's numerator, denominator and
null reason; hard-violation details; baseline/candidate deltas; and a gate result
of `blocked`, `failed`, or `passed` with reasons. A currently truthful report would
say `blocked: human adjudication, product thresholds and provider experiment pending`;
it must not invent measured scores.
