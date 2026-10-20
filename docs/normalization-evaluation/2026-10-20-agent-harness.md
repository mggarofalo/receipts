# Blind agent-harness evaluation — 2026-10-20

This experiment exercised the catalog decision prompt against actual model
responses. It did **not** exercise the application's Pi transport, OAuth grant,
production candidate retrieval, approval workflow, or the complete historical
catalog. It is diagnostic evidence, not a release quality threshold.

## Method

The evaluated policy was `receipts-catalog-v2-retrieval-v2` from revision
`ef7765a97c18b688fbd0ad3dc7c0d07c560e8f94`. Production `Instructions`, `Schema`,
and synthetic `BuildPrompt` output were obtained from the compiled Application
assembly. Every response was checked with production
`NormalizationCatalogAiPolicy.Validate`; scoring then compared the decision and
candidate identity with labels frozen before inference.

Each case received a fresh collaboration agent with no conversation history
(`fork_turns=none`). Only production instructions, the schema and one opaque input
were supplied inline. Expected answers, case names, splits, database identifiers,
financial fields and private artifact paths were withheld. No tools were used by
the model-under-test agents. There was one response per case, with no retries,
prompt changes or tuning between development and holdout cases.

The harness inherited its serving model. Its exact model revision, sampling
settings, token usage and provider latency were not exposed. The surrounding
agent system instructions also differ from Pi's `ModelRuntime` environment.
Consequently these results must not be attributed to a named Pi model or treated
as an exactly reproducible provider benchmark. Artifact timestamps include
orchestration and are not inference latency measurements.

The local Pi SDK was runnable and returned a model catalog, but no existing
Pi-owned credentials were found. Its isolated status probe returned
`auth_required`. No Codex credentials were extracted or copied into Pi, and no
interactive login was initiated for this experiment.

## Results

| Set | Responses passing production validation | Agreement with frozen labels |
| --- | ---: | ---: |
| Synthetic examples | 6/6 | 6/6 |
| Synthetic holdout | 18/18 | 17/18 |
| Private real-data development diagnostic | 15/15 | 15/15 |
| Private real-data holdout diagnostic | 15/15 | 15/15 |
| Separate structural diagnostic | 2/2 | 2/2 against acceptable-ID sets |

All 56 planned cases produced valid responses. These small, deliberately selected
sets are not random samples and do not establish population accuracy.

The synthetic set predicted nine `same`, six `different` and nine `insufficient`
decisions. There were zero false `same` decisions among the nine predicted
matches, relative to the proposed labels. Candidate-order, approved-example
ablation, injection and cross-scope controls agreed with their frozen
expectations.

The one synthetic disagreement was an abstention on a laundry description
containing `2X 1L`. The reference expected a distinct product; the response
identified ambiguity between concentration and pack quantity. This was not a
false merge. The original label and score were retained. The ambiguity warrants
independent adjudication rather than changing the reference to make a run pass.

The private primary set predicted five identity matches, six distinct products,
twelve abstentions, and seven incumbent-preserving `labelOnly` decisions.
There were zero false matches among the **five identity-match predictions**,
relative to the independently agent-reviewed labels. The seven label-only cases
stipulated the current identity and do not validate historical assignments.
All twelve expected abstentions remained abstentions, including cases with
missing retrieval candidates.

Three of the five identity matches proposed optional display labels. None of
the seven label-only cases proposed a change. Readability and preservation of
known attributes received a separate independent agent review: all three
optional labels were judged safe, modest improvements, and all six proposed
new names preserved the supplied attributes. The seven unchanged incumbent
labels were judged appropriate restraint against unsupported expansion.
This is agent-reviewed evidence, not user-adjudicated naming quality; identity
agreement alone is not naming-quality evidence.

The two structural cases supplied alternate spellings associated with distinct
PendingReview identities. These candidates are deliberately outside the
production Active-only retrieval contract. Both responses chose the exact
spelling's identity; one also suggested a more readable label. Both satisfy
their acceptable-ID reference, but they **did not consolidate the identities**.
A readable label does not merge purchase history or make reporting buckets one
identity. These two cases are excluded from the primary match denominator.

## Data boundaries and artifacts

The private set used a user-authorized, read-only backup export. Only minimized
descriptions and candidate evidence entered model prompts. Candidate lists were
lexical diagnostics, not results from an imported production pgvector retrieval
run. Legacy assignments were not treated as gold; label-only identity was
explicitly stipulated. No receipt or catalog records were changed.

Real product text, source scopes, local mappings and responses remain outside
the repository. Private artifact directories are
`%TEMP%/receipts-catalog-blind-eval-20261020` and
`%TEMP%/receipts-real-eval-46070cbb`. They contain input packets, opaque-ID maps,
raw responses, execution manifests and `results-running.json` journals.
The journal's `attempted` field counts captured response files; it is not an
independent dispatch ledger. All 56 planned responses are present in this run.
Future executions must record dispatches separately so missing responses count
as failures rather than disappearing from the denominator.

| Artifact | SHA-256 |
| --- | --- |
| Synthetic `catalog.v2.json` | `fc0e8c84b24a34f0b4e1a51a4a290aa4346ae4c3c202290346f3c2092a547339` |
| Frozen private reviewed corpus | `85a65dfde0e39388c297025e6145736579672ea977f2aa7e2e811b6b70f6dbd0` |
| Private primary provider projection | `0eb3b0f2b6c779eae0bfb20ace5a7186bc5afd46cdfb66c7a9f470e19ab7b649` |
| Synthetic scored journal | `cb9cd5d73ccc005c5f0328fd2d64a6a0645e51e5715b03f00d3ed970a0106b6e` |
| Private scored journal, including structural cases | `d6250cd0c41d7ecfca01aa559fd4f2813720fcb34fd329fc5ba85c51d3ded90d` |

## Remaining acceptance work and reuse

- Measure production retrieval recall separately using an isolated import,
  current eligibility rules, real embedding retrieval and frozen relevant-target
  labels. This experiment cannot establish it.
- Assess representative naming opportunities, especially abbreviated and noisy
  source descriptions. Three optional labels and seven no-change incumbent
  cases cannot establish catalog-wide readability.
- Review canonical consolidation separately from source assignment and display
  labels. The structural diagnostic illustrates that these are different
  responsibilities.
- Completing the historical catalog requires coverage, explicit review of
  uncertain and protected sources, and verification of history/report outcomes.
  Thirty selected real cases do not complete or certify that work.
- Verify a real Pi grant, the explicitly selected serving model, inference,
  refresh/revocation and application review/apply behavior in the deployed
  environment. This agent-harness experiment is not that proof.

For a repeat, freeze corpus, split, labels and policy hashes first; generate fresh
opaque IDs; keep one isolated context per case; retain every attempted response,
including failures; validate with production policy; and report identity,
label-only preservation, naming and retrieval metrics separately. Preserve the
original scores when later adjudication disputes a reference. The existing
`--chatgpt-evaluate-catalog MODEL example|holdout REPORT_PATH` command is the
bounded synthetic route for a future authenticated Pi run; private real data
must not be added to the checked-in corpus or public reports.
