# RECEIPTS-1056 pre-registered evaluation packet

Prepared 2026-10-26 before any new inference. Uses the TypeSafe skill, the existing RECEIPTS-1062 harness, and constructed data only. The earlier six-case probe is transport smoke evidence, not a quality benchmark. All 24 v1 rows are now tuning in corpus.v2.json. The 32 v2 held-out rows are fresh descriptions/source groups; related category concepts intentionally recur across splits. This is a designed challenge set, not a representative sample of production purchases. Expected labels are author proposals, not owner-adjudicated truth.

## Application policy and truth table

Taxonomy meanings, identities and hierarchy remain the v1 fixture. The application owns most-specific-supported selection, direct parent selection when detail is absent, and no inference from purchase instructions. Ancestors are derived in code. No automatic identity merge, mapping persistence or financial changes follow from a model answer.

| Input state / provider judgment | Application outcome |
| --- | --- |
| Existing explicit user mapping | Keep mapping; do not invoke Jev |
| Empty description or no usable candidates | No suggestion; do not invoke Jev |
| Transport error, timeout, invalid response, missing usage, wrong model, unknown category | Failure; stop experiment, no suggestion |
| Fit Noul <=0.3 | Abstain, no fit |
| Fit Noul >0.3 and <0.7 | Abstain, unresolved fit |
| Fit Noul >=0.7 with valid Choice | Show proposed category/path for explicit user acceptance |
| User accepts proposal | Normal user-controlled classification workflow |
| User rejects/dismisses proposal | Leave classification unchanged |

The current question policy remains classification-questions-v1, model jev-1.14.0. Choice probability/distribution and confidence are recorded, not treated as calibrated correctness gates. The fit Noul does not guarantee the selected category is certain. There is no automatic-write row.

## Proposed labels for independent review before dispatch

See corpus.v2.json for exact descriptions, expected outcomes and individual rationales. Cases v2h01-23 are category cases; v2h24-32 are abstention cases. There are 32 distinct held-out source groups. Coverage dimensions include animal/plant origin, lactose-free milk, broad dairy/food/household, yogurt/cream without dedicated leaves, plant cheese, synonyms, mixed sibling goods, personal care, pet food, opaque text, conflicting alternatives, and instruction text. Eight expected direct broad answers are v2h09-14 and v2h22-23.

Routine labels can be independently reviewed against the supplied definitions; do not require owner adjudication for explicit cow milk vs oat drink. Specific semantic questions to resolve in review: (1) Does a mixed paper/cleaner assortment use its common household ancestor? Proposed yes. (2) Does the constructed household-paper category exclude office printer paper? Proposed yes, based on its toilet-paper/towel/tissue definition, but the word 'supplies' is broader. If policy changes, version the category definition and corpus before any calls. (3) Does 'MILK OR SOAP ITEM UNKNOWN' identify any purchase? Proposed no because alternatives span branches. These are policy decisions rather than model errors.

## Frozen acceptance targets

These gates evaluate a limited assisted-suggestion pilot on this synthetic fixture only. Passing does not prove production accuracy, calibration or representativeness. Inspect every case and preserve disagreement details. Do not tune thresholds/questions after opening these held-out responses and then claim the same cases are held out.

Assisted suggestion technical go requires all 32 requests accounted for; zero failures/unattempted/unknown billing/model drift; total agreement >=29/32; selective category accuracy >=95%; at least18/23 category cases correctly suggested; 8/8 direct broad category cases correct; zero harmful descendants, wrong-branch suggestions or no-fit misclassifications (0/9 expected abstention cases classified). Any failed gate is no-go for that policy. Correction count is an error-count proxy, not measured human effort; no claim of time saved is permitted without a user study. Latency target p95<=2000ms and maximum<=5000ms; measured cost <=$0.10, with bounded reservation preventing overspend. A timing miss requires service/product review and cannot be hidden by accuracy.

Automatic classification is NO-GO for this packet regardless of results. A separate proposed future gate requires untouched owner-approved real-source sampling under separately disclosed authorization, explicit automatic-action policy, >=99% one-sided95% lower confidence bound on selected-category precision, zero harmful descendant/no-fit errors, and >=50% eligible-source coverage. With zero errors, at least299 independent classified sources are needed to reach a99% lower bound (0.05^(1/n)); 32 synthetic examples cannot do this. Persistence, rollback and operational safety checks are also required. Current user-visible behavior remains acceptance-controlled.

## Baseline and execution boundary

The independent deterministic baseline is leave unclassified for every unseen source: 0/32 suggestions, 9/32 total agreement, zero erroneous classifications. This is not a benchmark of the existing production resolver, nor does it prove Jev is better than alternative classifiers. No remembered mapping exists for these constructed sources.

Live docs canonical https://docs.typesafe.ai/models.md fetched2026-10-26: jev-1.14.0 input$0.042/M tokens, output free, default64k per-request/32k per-question. No long-context header. Worst-case reservation32*64000*$0.042/1000000=$0.086016, below the assigned$0.10 cap. This is conservative local reservation, not a provider billing lock. The worker has aggregate allowance <=60 attempted inference HTTP calls OR$0.10, whichever first; the reservation reduces the executable plan to32 calls. Zero calls until independent review of this packet. No retries, redirects, concurrent inference or fallback. Count failures before dispatch and stop on unexpected errors or missing usage. Never restart automatically.

Prepared command after review and focused harness tests:

```powershell
node scripts/classification-evaluation.mjs live --api-user-secrets --corpus docs/classification-evaluation/corpus.v2.json --split heldout --limit 32 --max-requests 32 --max-spend-usd 0.10 --out receipts-017-artifacts/1056/live-v2.json
```

TYPESAFE_API_KEY is read from existing API .NET User Secrets without printing it. Only synthetic descriptions and constructed taxonomy definitions are sent. No private purchase, account, store, date or amount details. Exact payloads, model/version, question policy, taxonomy/corpus hash, validated answer distributions, usage, latency and results are preserved. The wrapper already saves dispatch intent before each call. Failed usage stays unknown and stops further calls.

Offline dry-run packet: receipts-017-artifacts/1056/dry-run-v2.json. Usage ledger: receipts-017-artifacts/1056/usage.json. No commits or production changes in this preparation.

