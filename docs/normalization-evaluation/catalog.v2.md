# Catalog v2 evaluation protocol

Status: wholly synthetic, proposed labels, not human-adjudicated. This corpus is
an engineering diagnostic, not evidence of calibrated accuracy on real purchases.
It contains six example cases and eighteen holdout cases. Product families do not
cross the split. Editing a prompt after examining holdout failures makes that set
development data; create a fresh held-out set before claiming generalization.

The evaluator command is `--chatgpt-evaluate-catalog MODEL example|holdout REPORT_PATH`.
One provider request per case bounds a run to six or eighteen calls. The corpus
already includes explicit paired ordering and example-context cases; the CLI must
not silently multiply requests. Each call uses fresh opaque IDs, never case IDs,
expected decisions, knownMatchKey, tags, family or split. Only the description,
observed candidate names and approved examples with sameScope may enter the prompt.
No real receipt data is present. A live run uses the owner's model allowance and
requires the normal connected-account operation; no calls were made to author this
corpus. Validate fixtures locally with
`node --test scripts/normalization-catalog-corpus.test.mjs`.

## Metrics and denominators

- **Exact case result:** decision equals expected.decision; for same, selected key
  must additionally equal expected.candidateKey. Refusal, invalid schema, timeout
  and transport failure are explicit failures, not abstentions. Report counts and
  denominators, never silently discard failed calls.
- **Retrieval known-positive coverage:** among cases with non-null knownMatchKey,
  fraction whose supplied candidates contain that key. Null means no known match
  label and is outside this denominator. This is fixture diagnostic coverage, not
  measured recall of the production retrieval implementation. Real retrieval recall
  requires running production retrieval against a labeled seeded catalog.
- **Conditional matching accuracy:** report correct same selections among expected
  same cases where knownMatchKey is present. Also report performance over all cases
  with a known positive present, including evidence-conditioned abstention cases;
  name the two denominators explicitly rather than conflating them.
- **False match:** selecting same when expected is different/insufficient, or choosing
  a wrong candidate when expected same. Report count/rate over all attempted cases
  and over all same predictions. Abstention rate is separate from exact correctness.
- **Cost and performance:** record elapsed duration per request, returned usage,
  successes/failures, model and prompt version, corpus version, and total calls.
  Report p50/p95 with sample size; eighteen synthetic calls do not establish an SLA.

## Paired controls and label interpretation

`pair:franks-order` contains exactly the same three candidates in reversed order.
The target and expected verdict must remain unchanged. This is one perturbation
pair, not a broad claim of order invariance.

`ablation:pasta` and `ablation:coffee` remove the only same-scope evidence decoding
an otherwise opaque store abbreviation. Their expected decision deliberately changes
from same to insufficient. The hidden knownMatchKey describes the synthetic truth;
it must not reward the model for guessing that truth without observable evidence.
`ablation:pizza` removes a malicious instruction embedded in an example; both
conditions must select the visibly matching product. Report paired outcomes, not
only their aggregate score. These controls evaluate helpful and harmful contexts;
they do not prove every retrieved example is beneficial.

The scope controls contain identical abbreviations approved in a different scope.
They must not be treated as exact evidence of product identity here. Distinct
brand, pack, size, preparation, concentration and dietary attributes are intentional
product distinctions. Equivalent notation (1000ml/1L) does not change stored units
or financial amounts; this corpus only evaluates identity judgments.

## Before production claims

Independently review proposed labels, especially absent-attribute and new-product
cases, and record disagreements instead of adjusting labels to fit a model. Expand
with adjudicated examples representative of real stores and product frequencies.
Stratify precision, missed matches and abstention by risk slice. This schema contains
no model confidence score: it cannot establish probability calibration. If a future
decision adapter exposes probabilities, use a separate held-out calibration set,
reliability plots/Brier score and precision-versus-coverage analysis. No synthetic
score or confidence threshold authorizes automatic writes. Human-approved scoped
mappings remain the authoritative catalog decisions.

## Readable names are a separate outcome

The report retains each validated `proposedName` (a proposed distinct identity)
and `suggestedDisplayLabel` (a presentation change to an existing identity).
Neither field participates in identity/candidate agreement scores. A model that
correctly selects the existing abbreviated canonical name can score perfectly on
identity while making no progress on readable names.

`namingQuality.status` remains `unadjudicated`. Before claiming naming quality,
human reviewers must assess whether each proposal:

- Improves readability relative to the original canonical name and existing label.
- Preserves known brand, package size, units, flavor, preparation, and dietary distinctions.
- Avoids invented attributes or unsupported expansions of ambiguous abbreviations.
- Uses concise, consistent wording; accept equivalent valid phrasing rather than exact-string matching.
- Keeps global presentation changes separate from identity decisions and scoped purchase assignments.

Record disagreements and missing suggestions alongside accepted and rejected names.
Do not interpret an absent proposal as a verified readable name. Evaluate existing
abbreviated canonicals and already-mapped sources, not only newly discovered products.
This synthetic corpus supplies diagnostic identity scenarios; it does not yet provide
adjudicated naming-quality ground truth or a representative label-only workflow benchmark.
The production workflow separately reviews target revision and affected purchase count
before applying a global display name. Evaluation never approves those changes.
