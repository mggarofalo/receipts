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
gold for this experiment. Existing label files were not read or copied.

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
and parser differ. The snapshots are source pins, not a claim that a runnable
live controller has been built or verified.

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

Pricing/context/free-output assumptions could not be reverified because live
documentation retrieval failed. Before any later authorized dispatch, verify
those assumptions, actual available credit, all intervening spending and exact
production source/executable identity. Stop if USD 4.75 cannot cover prior
spending or the stated new reservation is no longer conservative. Bytes do not
establish token counts. Use the existing campaign reservation mechanism where
applicable; its offline ledger alone does not dispatch or guarantee billing.
No new enforcement infrastructure is requested here.

## Blind labels and scoring

Agent work can complete annotation and scoring without asking the user to label
anything. First have two independent agents annotate the frozen evidence-only
packet, without prior labels, provider predictions, probabilities or candidate
thresholds. A third prediction-blind agent adjudicates disagreements. Use only
description and supplied taxonomy. Record every justified useful full path,
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
separately. This packet needs independent evidence-matched labels; their present
status is pending, so no quality conclusion is currently available.

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
