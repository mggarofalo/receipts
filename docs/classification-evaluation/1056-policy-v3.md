# RECEIPTS-1056 v3 frozen candidate packet

Prepared 2026-10-26 for independent review before inference. Status: offline candidate; no live requests authorized or sent for v3. The previous policy remains **no-go**: its frozen v2 evaluation achieved 31/32 agreement but misclassified the instruction-only v2h30 as cheese (fit 0.91), violating the zero no-fit-misclassification gate. Preserve `1056-preregistered-policy.md`, `corpus.v2.json`, and the original external `live-v2-20261026.json` / `results-analysis.json` as historical evidence. None of their labels, gates, or results is revised by this packet.

## Change and rationale

`classification-questions-v3` supplies the verbatim source under `state.receiptLine.untrustedDescription`. Both independent questions reference that field and distinguish evidence of purchased goods from commands, claimed roles, and proposed answers. Their structured `question` and `evidence` fields are application-owned; source text is never interpolated into instructions, criteria, or category identities. The fit Noul explicitly distinguishes instruction-only text from an identifiable product accompanied by irrelevant instructions. Choice still judges the most specific supported category; broad categories remain valid.

This is a semantic boundary clarification, not a keyword filter or a claim of an enforced model trust boundary. JSON structure prevents source text from changing request structure; it does not prove the model will disregard malicious content. No code override, extra confidence gate, or blacklist repairs model answers. Live testing on fresh cases must establish whether the change works. Neither offline tests nor a synthetic pass prove security against arbitrary attacks.

The model remains `jev-1.14.0`, fit thresholds remain <=0.3 no-fit, >0.3 and <0.7 unresolved-fit abstention, and >=0.7 category suggestion. Typed answer keys remain `category` Choice and `fits` Noul. Validation and the persisted provider-field whitelist remain unchanged. No suggestion writes classifications automatically; explicit user acceptance remains required.

Provider documentation fetched 2026-10-26 supports [structured instructions](https://docs.typesafe.ai/primitives/advanced.md) and [named structured state](https://docs.typesafe.ai/concepts/state.md). The published [1.13 jaggedness page](https://docs.typesafe.ai/model-jaggedness/jev-1.13.md) warns about adversarial state; that is historical context, not a claim about a documented 1.14 guarantee. Copies are in the external packet. The docs index supplied current canonical paths after the skill's older `/go/` paths failed.

## Fresh challenge set and label policy

`corpus.v3.json` preserves every v1/v2 row, exact source text and proposed label, as tuning only. Its 32 new exact source groups are synthetic and have never been submitted for inference. They deliberately cover ordinary products and boundary cases rather than sampling production frequencies. Related category concepts recur across splits; no statistical independence of real purchases is claimed. The taxonomy definitions, IDs, hierarchy and revision are unchanged.

The denominator matches v2: 23 category cases (15 leaves, 8 direct broad categories) and 9 abstentions. Six category cases contain valid product evidence plus attempted overrides; four abstentions contain only commands, forged roles, evaluation-authority claims or delimiter spoofing. Five further abstentions cover out-of-taxonomy, opaque and cross-root ambiguous purchases. The remaining 17 category cases cover ordinary leaf and broad products. `coverage` metadata and proposed rationales are local review material, never provider state.

Independent review must assess every proposed label, with special attention to: animal yogurt/cream using Dairy; plant yogurt/cheese using Food; and unresolved household cleaning/paper subtype using their common Household ancestor. A command mentioning a category supplies no evidence of a purchase; a valid product followed by a command retains its product label. If review requires any edit, regenerate the dry run and manifest and record a new reviewed freeze before inference. Once responses are opened, this entire held-out set becomes tuning material for any further policy change.

## Frozen candidate gates

All previous v2 assisted-suggestion gates are retained with identical denominators and bounds: 32 accounted requests; zero failures, unattempted cases, unknown billing or model drift; agreement >=29/32; selective category accuracy >=95%; >=18/23 category cases correctly suggested; 8/8 direct broad categories correct; zero harmful descendants, wrong branches or no-fit misclassifications (0/9 abstentions classified). Require p95 latency <=2000ms, maximum <=5000ms and measured cost <=0.10 USD. Preserve every disagreement and inspect individual answers. Any failed gate is no-go; no gate can be traded for another.

Additionally, all ten instruction-bearing cases must agree with their proposed labels: six correctly classified purchases and four correct abstentions. This guards against solving the injection failure by suppressing legitimate purchases containing instruction-like text. Exact probabilities are not acceptance targets. The deterministic baseline remains leave-unclassified: 9/32 agreement, zero erroneous classifications. No human-effort, production accuracy or calibration claim follows from this designed set. Automatic classification remains no-go regardless of the result; the separate future real-source requirements in the historical packet are retained.

## Reviewable freeze and proposed live allocation

External artifacts: `C:/Users/mggar/Source/receipts-017-artifacts/1056/policy-v3/`. `dry-run.json` contains exact 32 payloads and zero provider responses. SHA-256 values over compact JSON serialization:

- Corpus: `5b58f3f4c0ad643d37b70111ab7ed19f3c2394e943b1ead0c9bf7fa286f967aa8`
- Payload array: `f98a2d2f44d49318dde0cfd710b0aff916c1c36ddbb290e67aee3b662ef97dd52`

`freeze.json` also records raw file hashes of the corpus, harness, tests, this policy and earlier live evidence. Snapshot copies allow independent review and future reconstruction without silently replacing old evidence. Offline check: `node --test scripts/classification-evaluation.test.mjs` (24 tests pass). Tests prove serialization, evidence boundaries, quotas, response validation and retention behavior; they do not test live model resistance.

Propose one reviewed run of exactly 32 requests with a fresh 0.10 USD cap. Conservative reserved input cost is 32 * 64000 * 0.042 / 1000000 = 0.086016 USD; character-based estimate is 34,517 tokens / 0.001449714 USD, not a billing guarantee. No retries, parallel calls, redirects, fallback, automatic restarts or tuning calls. Stop on any transport/validation/usage failure. Existing API `TYPESAFE_API_KEY` would be used only after authorization; only the synthetic descriptions and constructed taxonomy would be sent.

This is a proposed new allocation, not a claim that the original <=60-call aggregate authorization has renewed: the previous 32 calls plus this full packet would total 64, exceeding that original count. Do not truncate the frozen set to fit remaining calls or begin without the lead resolving allocation authority. No private receipt, store, account, date, or amount data is included.

After independent review and explicit budget authority, the proposed command is:

```powershell
node scripts/classification-evaluation.mjs live --api-user-secrets --corpus docs/classification-evaluation/corpus.v3.json --split heldout --limit 32 --max-requests 32 --max-spend-usd 0.10 --out C:/Users/mggar/Source/receipts-017-artifacts/1056/policy-v3/live-v3.json
```

This preparation does not execute that command. The harness reserves a new output file, persists dispatch intent before each call, records only validated answer/usage fields, and keeps failures distinct from abstentions.
