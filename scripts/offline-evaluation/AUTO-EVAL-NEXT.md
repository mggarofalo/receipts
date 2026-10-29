# RECEIPTS-1076: frozen automatic-classification pilot

Prepared 2026-10-29 against main `20b13bf44108693e063ff8c61de69b26b4e5a4d0`
(#838). No live execution, provider calls, production writes, or rollout is
authorized by this packet. Private evidence lives outside git in
`C:/Users/mggar/Source/receipts-017-artifacts/real-eval/auto-eval-next-v1`.
The private `freeze.private.json` binds membership, policy, evidence-only
annotation packet and six production source snapshots. No production feature
is introduced. The offline-only executable adapter is
documented in `tools/Receipts.JevProductionParity/README.md`; it validates the
packet and can replay exact recorded HTTP responses through production parsers,
using an in-memory handler that cannot send network requests.

## Membership and evidence

Select 24 cases from the 36 remaining families of the existing frozen 48-family
holdout. Exclude the **entire** previous 12-case selected-path holdout, including
its unattempted suffix, and all source-only matches in recorded dispatch intents
and journals. Development and the previous partial holdout are consumed. Order
remaining families by SHA256 of the declared seed plus frozen family hash; take
the first 24 without reading labels or results. The private manifest records the
seed, input hashes, dispatch inventory and source membership. There are 24
distinct frozen product families and no source-cluster overlap with the other
frozen families. Do not replace failures or undesirable cases.

The inherited grouping connects aliases, source clusters, codes, shared
informative tokens and similar strings. This is conservative lexical separation,
not proof of semantic independence or absence of outside exposure. Original
exclusions removed a large connected component: this is a curated challenge
sample, not a population prevalence sample. All real descriptions, taxonomy,
source IDs, store information, provider bodies and rationales remain private.

Both arms receive **description only**, the same ordered active taxonomy and
revision. Store and product code are absent. Blank definitions use the existing
category-name fallback. Historical assignments and contextual labels are never
gold for this experiment. Existing label files were not read or copied during
membership selection. The pre-prediction amendment below binds the existing
independent evidence-only adjudication for scoring.

## Frozen decision and production path

The baseline is the diagnostic outcome before the selected-path check: use
`JevSuggestionQuestions.Serialize`, `JevSuggestionProvider.SuggestBoundAsync`
and `CategorySuggestionPolicy.Decide` from the pinned production source. Global
fit below 0.7 abstains; global fit at least 0.7 follows the real hierarchical
choices, including supported parent stops. No confidence or terminal-choice
probability gate is added.

The treatment reuses that exact baseline response and selected path. On a
baseline assignment only, call the production
`JevSuggestionProvider.VerifySelectedBoundAsync`, through the actual
`JevCategoryAssistanceSelectedProvider.VerifySelectedAsync` binding when using
the assistance path. This uses production `JevSelectedPathQuestions.Prepare`
and its bound parser. Its state is description only, with store and product code
null. Keep an assignment only when selected-path support is at least 0.7.
Do not substitute draft #834's experimental selected-path exchange: its binding
and parser differ. The snapshots are source pins. A prepared live controller
and synthetic transport checks are documented in the parity tool README;
they provide no real model-quality or account-credit evidence.

The candidate auto-write policy is to simulate writing that unchanged category
and full path if both production gates pass. A valid abstention makes no write.
Any disabled/revoked/stale authorization, failed transport, invalid or unknown
response makes no write and stops the run. No fallback, response repair,
retry, taxonomy mutation, threshold selection, or real database write is part
of the experiment. The 0.7/0.7 gates are inherited hypotheses, not calibrated
automatic-write safety guarantees. Automatic classification remains disabled.

## Budget and run accounting

Use one sequential paired run: at most **24 baseline attempts plus 24 conditional
selected-path attempts = 48 total**, with zero retries and zero extra model
labeling calls. The two arms share the baseline; this is not two independent
baseline runs. A crash, unknown or failed attempt retains its reservation. Stop
on the first failed, invalid or unknown response; keep unattempted suffixes in
the report and do not resume this holdout for tuning.

Under the existing repository assumption of 65,536 input tokens per attempt at
USD 0.042/million and free output, reserve USD 0.002752512 per attempted request:
**USD 0.132120576 maximum new reservation**. Provisionally reserve **USD 4.75 for
all prior/uncertain spending**, so the combined reservation is USD 4.882120576
within the approximately USD 5 credit, leaving USD 0.117879424. The local journal
inventory contains 159 attempt records across 13 journals, including duplicated
checkpoint history; the maximum recorded carry is USD 0.72. Even pessimistically
adding all 159 full reservations to that carry is USD 1.157649408, below USD 4.75.
This is a conservative local inventory, not an account-balance or billing audit.

**Pre-prediction budget amendment, 2026-10-29:** the USD 5 amount is the user's
cumulative spending authorization, not verified remaining provider credit. Keep
the USD 4.75 prior/uncertain carry and the 48-attempt, USD 0.132120576 maximum
new reservation unchanged. The combined USD 4.882120576 allocation stays below
USD 5; it must not also be required as remaining available credit. Historical
actual billing and intervening spend are uncertain; the local inventory above is
not comprehensive account evidence. No balance API or account UI is available
in this workflow, so the controller requires no unverifiable balance attestation.
Budget authorization alone does not establish sufficient remaining credit.

The official model documentation was rechecked before predictions: Jev input
USD 0.042/million, output free, default 64k context, no long-context header
([TypeSafe models](https://docs.typesafe.ai/models.md)). The approval records
pricing/context verification separately from the cumulative budget authorization.
Verify exact source/executable identity before dispatch; stop if known additional
spending invalidates the conservative carry or pricing changes invalidate the new
reservation. An insufficient-credit HTTP response stops further sends and resume,
retains the attempt reservation and leaves the suffix unattempted, with no retry.
This amendment changes approval accounting only, before any current predictions;
membership, policy, thresholds, scoring and acceptance gates remain frozen. Bytes
do not establish token counts, and local reservation does not guarantee billing.

## Blind labels and scoring

**Pre-prediction amendment, 2026-10-29:** reuse the existing independently
blinded provisional A/B annotations and prediction-blind adjudication rather
than create a replacement oracle. A metadata-only integrity audit established
coverage of all 24 selected cases and that the original adjudication was frozen
before predictions. This amendment and the private `scoring-freeze.private.json`
were prepared before reading or generating any current predictions. The private
freeze binds the original membership, packet freeze, policy, and these exact
label file SHA256 values:

- Independent A: `fa3b5fc47cac98d69d7c05d2515fbfde774aae5a0cc946b6abb08a7b7b103ed9`.
- Independent B: `216566f38376a893e06b1d7fab4b11e08859f9a29e412088932289f4d046391d`.
- Adjudicated: `f5970a0d140ca50191095be2548d25a4b6caf8f73f1e580fcaeffdc3d91143edc`.

These files cover the earlier larger annotation cohort; join only the original
24-case membership, never substitute or select cases by labels. No membership,
evidence, production policy, threshold, budget, acceptance gate, or original
freeze is changed. Label content stays private and absent from prediction state.
No real predictions are scored by this preparation, and no provider calls occur.

The agents used only description and supplied taxonomy, without provider
predictions, probabilities, or candidate thresholds. Record every justified useful full path,
including a broad parent when supported; do not mechanically credit all
ancestors. Mark no-answer when no useful category is supported and unresolved
when evidence or taxonomy cannot support an adjudication. Freeze label and
adjudication hashes **before joining predictions**. Keep rationale private.
No additional Jev or other paid-provider annotation requests are budgeted.

After annotation, a separate scoring agent joins frozen outcomes and labels.
Report useful assignments, wrong branches, unsupported descendants (harmful
specificity), other unsupported assignments, valid abstentions, failures,
unknowns and unattempted cases for both arms. Multiple harm flags can coexist;
use harmful specificity first for mutually exclusive error counts. Report
gold no-answer and broad-only case counts, writes on each, useful broad answers,
missed useful answers, correct no-answer abstentions, baseline errors suppressed,
useful baseline answers suppressed, and selected-path calls actually attempted.
Show the paired transition counts, not just an aggregate accuracy. No-answer
and unresolved are distinct; never turn provider failure into abstention.

Denominators: useful coverage is useful/24; write coverage is writes/24;
selective accuracy is useful/writes (null if no writes). All 24 remain in the
overall denominator. Show provisional/ambiguous-label counts and sensitivity
separately. The reused oracle is provisional agent judgment, not human gold:
initial passes disagreed on 15/24 cases; adjudication marks 23 labelable and one
requiring abstention. Adjudication does not remove that uncertainty. Report
disagreement/uncertainty strata and sensitivity separately; do not relabel after
seeing outcomes. No model-quality conclusion is currently available. Successful
synthetic scorer checks establish accounting behavior only.

### Prepared aggregate scorer (offline only)

`aggregate_scoring.py` reuses the existing evaluator's full-cohort accounting
and rate calculations. It accepts the production `OfflineReplay` artifact,
original private cases, adjudicated labels, private scoring freeze, exact recorded
exchanges, and an explicitly supplied built production parity DLL. The CLI
verifies case/label byte hashes, then regenerates the entire replay from those
exact frozen packet policy, cases and exchanges using the socket-free production
`offline-replay <policy.private.json> <cases.json> <exchanges.json> <output.json>`
command. The policy file's SHA256 must match the scoring freeze before replay. The submitted replay must equal the regenerated artifact in full before
labels are loaded for joining. Every exchange fingerprint must match its regenerated
request, including conditional selected-path requests; no trailing exchanges may
be ignored. Verify the DLL's source/build identity against the pinned production
implementation before use. This proves replay consistency with supplied recorded
evidence, not independent proof that a provider actually sent those bytes; retain
the live controller's durable journal as private provenance.
It writes only aggregate counts,
paired transitions, provisional-label strata and sensitivity counts; it never
prints descriptions, taxonomy, identifiers, labels, predictions, or parse excerpts.
Use explicit private paths outside the repository for every input and output:

```powershell
python scripts/offline-evaluation/aggregate_scoring.py --cases <private-cases.json> --labels <private-adjudicated-labels.json> --policy <private-policy.private.json> --replay <private-replay.json> --freeze <private-scoring-freeze.json> --exchanges <private-exchanges.json> --runner <built-production-parity.dll> --output <new-private-aggregate.json>
python -m unittest discover -s scripts/offline-evaluation -p test_aggregate_scoring.py
```

Exchanges use the existing `ReplayExchange[]` shape (`body`, `statusCode`,
`requestSha256`). Extract the live controller journal's paired intent/response
records offline, binding every reservation and fingerprint to the submitted replay
(1 to 96 attempts, the campaign ledger's per-journal maximum):

```powershell
python scripts/offline-evaluation/journal_exchanges.py --journal <private-live.journal.jsonl> --replay <private-replay.json> --output <new-private-exchanges.json>
```

The converter preserves exact bodies, order, HTTP statuses and fingerprints.
It rejects incomplete JSON lines, extra records, missing reservations and drift;
only a reserved final intent with a replay `unknown` attempt may lack a response.
Keep the original journal as private provenance. A missing live report after a
crash requires separate offline reconstruction and investigation; the converter
does not resume dispatch or repair journals. The scorer then regenerates and
compares the entire production replay before joining labels. Do not
run the live command during scoring. Temporary regenerated evidence is created
beside the private output and removed afterward. Build the parity DLL first to
run the synthetic socket-free CLI integration checks; those checks explicitly
skip when the DLL is absent.

This is preparation only; no real replay is loaded or scored. Full paths must
match the frozen taxonomy. Credit only explicit maximally specific paths,
explicit supported coarse alternatives, and explicit valid broad ancestors.
An unsupported strict descendant of an accepted path is harmful specificity;
an unsupported path unrelated by ancestry to every accepted path is a wrong
branch. The mutually exclusive error category prioritizes harmful specificity;
flags are also counted separately. A no-answer write is other unsupported.
An unresolved-label write is retained separately, never credited or silently
treated as wrong gold. Missing/misaligned records fail closed; explicit stopped
suffixes remain unattempted. Unknowns remain separate from failures and valid
abstentions. The 24-case denominator is enforced, including a stopped run.
Require one sequential baseline attempt per attempted case and exactly one
conditional selected-path attempt following each successful baseline assignment.
Global abstentions skip that attempt. The stopping failure/unknown keeps its
reservation; no attempted suffix, duplicate, extra, reordered, unbound or omitted
attempt is accepted. Both stages are capped at 24, with at most 48 total; counters
and reservations must match the actual attempt list. A missing response at the
exact stopped tail can remain unknown without manufacturing a successful exchange.
Agreement-only and unambiguous useful coverage conservatively withhold disputed
or uncertainty-marked credits while retaining denominator 24. These sensitivity
counts do not resolve oracle uncertainty or establish acceptance/rollout safety.

## Preregistered acceptance counts

All are required for a successful **pilot**, with no post-hoc threshold changes:

- Exactly 24 completed cases; zero unresolved labels, failed/unknown outcomes or
  unattempted cases. A stopped run is incomplete, even if its prefix looks good.
- At least 12 useful treatment assignments; zero incorrect treatment assignments,
  zero wrong-branch assignments and zero harmful-specificity assignments.
- Retain at least 90% of useful baseline assignments: suppress no more than
  `floor(0.1 * baselineUseful)` useful baseline assignments. For example, with
  18 useful baseline assignments, at most one may be suppressed.
- Zero writes on gold no-answer cases and zero unsupported descendants on
  broad-only cases. If either stratum is absent, that safety question remains
  untested and automatic rollout is not accepted. Report error suppression even
  if the baseline makes no errors; do not invent a benefit denominator.

These are necessary pilot gates, never sufficient rollout evidence. Even zero
errors in 24 independent assignments has a one-sided 95% binomial error upper
bound of about 11.7%; with only 12 assignments it is about 22.1%. Correlated
families and challenge sampling further limit inference. Additional untouched
evidence and explicit product acceptance are needed before automatic writes.
Once inspected, this cohort is consumed for future tuning or threshold selection.

## Generalized scoring (owner-label packets)

The scorer no longer hard-codes this pilot's 24 cases, 48 attempts or USD 4.75
carry; every cohort-specific value comes from the scoring freeze. Packets from
`backup_cohort.py` use owner-derived labels (each item's current assignment,
provenance `owner-current-assignment`) and a labels file shaped
`{"oracle": "<description>", "labels": [...]}`. The nonempty `oracle` string is
copied into the report instead of a hard-coded oracle description.

The private scoring freeze has exactly this shape and is validated strictly:

```json
{"beforeCurrentPredictions": true, "cohortSize": 12, "maximumAttempts": 24,
 "priorCarryNanodollars": 4873863040,
 "candidateGates": {"globalFit": 0.9, "selectedPathSupport": 0.8},
 "acceptance": {"minimumUsefulFraction": 0.5, "maximumUsefulSuppressionFraction": 0.1},
 "sha256": {"membership": "<cases sha256>", "adjudicatedLabels": "<labels sha256>",
            "policy": "<policy.private.json sha256>"}}
```

`cohortSize` is 1-48 and `maximumAttempts` must equal twice it. Candidate gates
are finite and within [0.7, 1]. The per-attempt reservation stays USD 0.002752512.
The replay's prior carry must equal the freeze's carry.

Three arms are reported: `baseline`, `treatment` (production selected-path
check at 0.7), and `candidate`, the preregistered two-signal auto-write rule.
For a completed case, the candidate writes the treatment's category and path only
when the treatment has a category, the **baseline** `fitProbability` is at least
`globalFit`, and the treatment `selectedPathSupport` is at least
`selectedPathSupport`. A missing or non-finite signal does not meet its gate.
Otherwise the candidate abstains. Failed, unknown and unattempted cases mirror
the treatment. Paired transitions are reported for baseline -> treatment and
baseline -> candidate, with error and useful-answer suppression for both.

`gates.candidate` and `gates.treatment` evaluate the preregistered acceptance
counts above with frozen fractions. Each gate is `pass`, `fail` or `untested`:
`complete`, `minimum_useful` (at least `ceil(minimumUsefulFraction * N)`),
`zero_incorrect_writes`, `retention` (at most
`floor(maximumUsefulSuppressionFraction * baselineUseful)` suppressed),
`no_answer_safety` and `broad_only_safety` (unsupported descendants only;
`writes_on_broad_only` still counts correct writes and is reported separately).
`overall` is `pass` only with no failed or untested gate, `fail` if any gate fails,
otherwise `incomplete`. Owner-derived labels are always labelable with one
specific path, so they have no no-answer or broad-only strata. Those two gates
are `untested` and `overall` cannot pass. This is intended: the separate
synthetic-safety set must cover those safety strata. `untestedReason` says so.
