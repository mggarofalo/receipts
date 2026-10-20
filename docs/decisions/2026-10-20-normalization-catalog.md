# Normalization catalog: approved mappings and bounded model decisions

Date: 2026-10-20. Status: implemented design; live-model acceptance remains pending.
Tracking: [RECEIPTS-1020](https://plane.wallingford.me/dev/projects/aaac8dc9-bc4c-42db-ac99-eee7864c78e9/issues/6a14bf1c-64aa-4d56-b0ae-f77f7831339a).

This records the implemented design and verification obligations. The PR and
tracked acceptance evidence record which checks have passed. The existing review-first ChatGPT work in PR #762 is
an input to this change. Live inference, catalog acceptance and specialist-model
benchmarks require their own evidence.

## Problem and decision

Per-purchase suggestions do not create a maintained catalog. A user needs an
approved interpretation of a receipt description to cover appropriate history and
future identical purchases, including when ChatGPT and embeddings are unavailable.
At the same time, similar strings can describe materially different products.

Application owns the mapping policy, identity distinctions, approval eligibility,
provenance and correction rules. Infrastructure owns efficient retrieval, durable
history execution and atomic persistence. Existing canonical foreign keys remain
the authoritative grouping used by search and reports. React presents distinct
source descriptions, affected counts, review and operation progress. Neither a
model response nor a retrieval score authorizes a write.

## Scoped approved mappings

Use a conservative key consisting of store scope, normalized raw description and
optional source item code. Missing codes are explicit absence, not wildcards.
Normalization of this key must not discard identity-bearing numbers, units,
flavors, preparation or dietary distinctions. A mapping at one store must not
silently teach another store that a similar abbreviation means the same product.

Approval records the exact key, canonical target, actor, revision and origin.
Conflicting approvals have one authoritative database winner. Approved exact reuse
is an offline deterministic capability; embedding or provider availability must
not be required. Approximate matching may propose candidates but must not promote
an unreviewed alias into the approved catalog. A chain of approximate matches is
not sufficient evidence to merge a cluster.

Preserve explicit manual assignments, template decisions and rejected decisions.
Every assignment writer must record its actual provenance; updating only the AI
apply path would leave the catalog unable to distinguish protected work. Existing
links migrate to `LegacyUnknown` with their targets unchanged. This name means the
origin is unknown, not that the assignment is wrong or safe to replace. Legacy
reassignment requires explicit inclusion in preview and confirmation.

Changes to parent receipt location alter mapping scope and must participate in
revision fencing, alongside item and canonical revisions. Concurrent edits,
soft-deleted sources or targets, and changed mapping versions must be rechecked
when applying a preview. Receipt financial facts are outside this operation.

## Historical preview, propagation and correction

Preview distinct scoped descriptions and their occurrence counts, eligible counts,
protected counts and legacy counts before confirmation. Capture the approved
scope and relevant revisions; a broad search performed later is not equivalent
to the user's reviewed selection.

Historical propagation is bounded, durable and resumable, with visible partial
results and cancellation. Completed work is not repeated after restart, and a
retry must not duplicate audits. Preserve existing receipt writer, normalization
locks, audit, soft deletion and projection invalidation obligations. Prices,
quantities and currencies remain unchanged as grouping becomes visible in search
and price history.

Revocation stops future exact reuse. Undo/correction is conditional: restore only
assignments still attributable to the affected mapping operation/version and still
at the expected revision. Later manual edits, later approvals and changed scope
must survive; expose skipped/protected counts instead of pretending everything was
reverted. Keep enough before/after provenance to explain and safely reverse the
operation. Coverage must distinguish reviewed, automatic, legacy, unresolved and
excluded items.

## Retrieval and model judgment are separate

Use exact approved mappings first. Hybrid retrieval over active canonical entries
and approved examples supplies a bounded candidate set for unresolved inputs.
Rejected or unapproved examples cannot quietly become trusted demonstrations.
Snapshot candidate IDs and the disclosed evidence before dispatch; reject model
references outside that set.

A matching decision has three meaningful outcomes: `same`, `different`, and
`insufficient_evidence`. Missing attributes must not be invented. An empty candidate
set or an abstention is not proof that a new canonical item should be created.
Separate selection of an existing canonical item from generation of a proposed new
name. The existing ChatGPT runtime can implement this bounded decision pattern;
no additional paid provider is selected by this design.

Approved examples can help interpret store abbreviations, but can also anchor the
model to a wrong product, amplify old mistakes or crowd out decisive evidence.
Evaluate canonical-only versus canonical-plus-example inputs as an ablation, with
fixed candidate sets and labels. Separately measure whether example retrieval
improves candidate recall. Do not assume that adding more context improves quality.

## Identity and readable names have separate approval boundaries

Reusing a raw canonical identity must not freeze an abbreviated label forever.
Model output may separately propose a readable display label for an existing
identity. Global label review exposes its affected purchase count and target
revision; approval preserves canonical identity and embeddings. Mapping approval
does not implicitly approve naming. Explicit label-only requests allow existing
mapped/protected sources to be curated without relaxing assignment protections;
a source with multiple linked identities is rejected rather than choosing an
arbitrary representative. Search and report drill-downs must recognize the same
display label. Naming quality needs human evaluation distinct from identity
accuracy, including preservation of known brand, size, flavor and dietary facts.

## Specialist models considered

Checked against primary documentation on 2026-10-20:

| Model | Verified capability | Constraint for Receipts |
|---|---|---|
| [OpenAI Luna Decisions](https://developers.openai.com/api/docs/guides/decisions) | Public-beta `POST /v1/decisions`, `gpt-6-luna`, predicate/choice/score; published input price $0.10/M tokens, no output/cache charges. | A separate API capability; not established as part of the current ChatGPT-plan credential entitlement. |
| [TypeSafe Jev](https://docs.typesafe.ai/models) | `POST /v1/systemone`, typed questions; versioned 1.13/1.14 models; $0.042/M input tokens, free output. | Separate provider credentials and data-sharing decision; pin the evaluated version rather than a moving alias. |
| [Cloudflare Clef](https://developers.cloudflare.com/workers-ai/models/clef/) / [Clef-flash](https://developers.cloudflare.com/workers-ai/models/clef-flash/) | Hosted 27B/9B decision models; $0.24/$0.038 per M input tokens, free output. | Hosted use needs its own provider credentials; local use needs independent serving and hardware verification. |

Prices and availability can change. Provider speed claims and public benchmark
rankings do not establish receipt-specific accuracy, latency or total operating
cost. No account, paid call or new provider is authorized merely by this comparison.

[Official ChatGPT-plan inference documentation](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)
describes Responses inference and account-specific models, not Decisions access.
The current pinned Pi `openai-codex` text runtime is distinct from that registered
SIWC flow; see the [deployment decision](2026-10-19-chatgpt-normalization-topology.md).
A model appearing in a third-party catalog does not prove account entitlement or
an official transport contract.

[Cloudflare's Clef-flash model card](https://huggingface.co/Cloudflare/clef-flash)
licenses the weights under Apache-2.0 and documents a specialized joint schema head
and GPU serving examples. Generic chat inference or quantization cannot be assumed
to preserve that head and its probabilities. No Raspberry Pi performance promise
is justified without measurement. Open weights do not remove serving, memory,
maintenance or license-notice obligations.

## Evaluation and acceptance

Measure candidate recall separately from decision accuracy conditional on the
candidate being present. Include known matches absent from retrieval, no-match
cases, ambiguous abbreviations, conflicting variants, missing codes, different
stores, distractors and changed candidate ordering. Hold out labeled product
families/store patterns from prompt development to avoid testing memorized aliases.

Report false matches, missed matches, abstentions and precision/coverage curves,
not only average accuracy. Evaluate probability calibration on held-out human
labels before selecting thresholds. TypeSafe explicitly defines its
[confidence statistic](https://docs.typesafe.ai/confidence) from the distribution
of options; that is not independent proof of correctness. Do not compare this
number directly with embedding similarity or another provider's score. No
confidence threshold enables automatic model-driven writes in this design.

Repeated agreement is also insufficient: a
[decision-stability study](https://arxiv.org/abs/2609.27678) on legal judgments
found that aggregate scores can hide changed decisions and repeated answers can
preserve errors. This is motivation for perturbation tests, not evidence about
Receipts quality. Preserve held-out expected results when a model fails.

Required implementation evidence includes delegated backend/UI tests; real
PostgreSQL approval conflicts, revision fences and soft-deletion behavior; offline
exact reuse with both AI capabilities unavailable; cancellation/restart and audit
idempotency; safe undo after later edits; and an end-to-end history/new-purchase
search scenario. Targeted mutations should demonstrate that scope, provenance and
parent-revision protections detect regressions. Specialist comparison additionally
measures end-to-end p50/p95 latency, actual usage, retries and review workload on
the same corpus. None of those pending results is implied by this design note.
