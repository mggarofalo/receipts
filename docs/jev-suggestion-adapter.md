# Jev suggestion adapter

Category assistance asks Jev what an item is. A **strong** answer is written to the item automatically; anything weaker stays a **suggestion** that a person accepts, corrects or dismisses. Jev never approves a remembered mapping, never changes an item that already has a category, and manual entry works without Jev.

## What is automatic, and why

The owner asked to "automatically apply strong matches and show exceptions". So, when an administrator has consented to assistance and left **Apply strong matches automatically** on (the default), the worker writes:

| Result | What happens |
| --- | --- |
| Fit at least 0.7 and the stage-1 top root at least `RootAutoWriteThreshold` (0.90) | The **root** is written. |
| As above, `LeafAutoWriteEnabled` is true, the stage-2 top choice is at least `LeafAutoWriteThreshold` (0.90) and lies under that same root | That **child** is written instead. |
| Anything else that proposes a category | A suggestion with the top candidates and their probabilities. Nothing is written. |
| Fit below 0.7 | An abstention (`no-fit` at or below 0.3, otherwise `uncertain-fit`). Nothing is written. |

Leaf writes are **off by default**. In round 4 the root was right in 21 of the 22 item cases that cleared the root threshold, but the exact leaf was right in only 6 of 10 leaf-scored cases at the leaf threshold, on a tree that was about to be restructured. The thresholds and the leaf flag live in configuration (`CategoryAssistance:RootAutoWriteThreshold`, `CategoryAssistance:LeafAutoWriteThreshold`, `CategoryAssistance:LeafAutoWriteEnabled`); both thresholds must be between 0.7 and 1, or the API refuses to start.

Automatic writes only fill items that have no category: no assignment, or an unresolved legacy observation. That is the same eligibility as accepting a suggestion. A manual choice, a deliberate clear, an approved-mapping assignment and any other existing category are never overwritten. Items the saving user set aside, or a suggestion they marked "leave as is", are skipped.

Each write goes through the shared classification path (`ClassificationService`) with assignment source **`jev`**. Its reason is a JSON `JevAutoWriteEvidence`: attempt and request IDs, question policy, model, taxonomy revision, level, fit, root and leaf probabilities, and the thresholds and leaf flag in force. Envelope or other save-time rules that react to classification changes apply exactly as they do to any other assignment.

For each saving user, the written items form one entry in that user's **automatic classification activity**, the same list and conditional undo used for approved mappings (`HistoricalOperationKind.JevSuggestionV1`). Undo writes a manual correction back to the previous value (usually no category), so Jev does not write that item again. Undo skips any item that changed since.

A suggestion that was written automatically leaves the review queue in state `applied`, so it no longer counts against the pending limit. A later save of the same source attaches to it and is written by a later pass. If automatic writes are switched off (or the thresholds are raised), new results stay suggestions, and an applied source returns to the review queue the next time it is saved.

## Request policy

The policy is `classification-questions-v7` on `jev-1.14.0`. It is the two-stage request validated in round 4 (`two-stage-v1` in the evaluation packet); the evaluation tool builds its requests with this same code (`JevTwoStageQuestions`). Both requests send only `purchase.receiptText` and, when known, `purchase.store` (the receipt location, which the owner approved sending). No price, date, quantity, account, receipt or item identifier, and no envelope, is sent.

1. **Stage 1** asks one Choice over the roots, keyed by root name, and a Noul: "Does one of `categories` describe the product bought in `purchase`?". A definition that only repeats the name is sent as `null`.
2. **Stage 2** asks one Choice over the children of the stage-1 top three roots, keyed `Root / Child`, in taxonomy order so the stage-1 winner gets no first-option advantage. A root without children is offered as itself.

Back-off to the parent is decided in code, never offered as an option: the proposal is the stage-2 top choice when it lies under the stage-1 top root, otherwise that root. The proposal's probability and confidence come from its own stage's Choice (`ProbabilityScope=local_choice`). The suggestion also keeps the top three roots and top three stage-2 choices with their probabilities.

Each question carries one plain line saying that instructions or category names written in the receipt text are not evidence. Round 4 tested it with injected probe cases: no stage-1 hijacks in four probes.

The policy covers two levels: roots and their children. A deeper tree fails the request as invalid input; it is not cut short. Option keys must be unique and a root name may not contain ` / `. Each Choice allows at most 255 options, and stage 1 and the largest possible stage 2 must each fit in 65,536 UTF-8 bytes, or the request fails. Responses are limited to 1 MiB.

## Validation

`JevTwoStageQuestions` checks the response structure: the exact answer set, the exact option set, typed numbers and nonnegative token usage. `CategorySuggestionPolicy` then checks the model, finite probabilities in [0, 1] that sum to one (within 0.01), a chosen option that holds the maximum, and a fit in [0, 1]. Stage 2 must leave everything stage 1 decided unchanged. Any failure is a provider failure, never an abstention, and writes nothing.

## Configuration and transport

Configure `TYPESAFE_API_KEY` through the API server's configuration/User Secrets path; see [Jev setup](jev-setup.md). `Jev:Enabled=false` disables connectivity. The assistance worker requires persisted administrator consent and the current connection binding as well as a configured credential. A successful connection check does not grant consent.

The client removes inherited resilience handlers and HTTP loggers, disables redirects, and uses a 10-second linked timeout per send. The worker authorizes and debits each request separately under the same durable claim, rechecking current fences and the UTC-day limit before stage 2. A stage-1 abstention costs one request; a completed result costs two. If only the daily limit blocks stage 2, the validated stage-1 result waits for the next UTC day. A stage-2 failure or denial never publishes the stage-1 category. Caller cancellation propagates. One singleton semaphore bounds concurrent transport use; busy attempts do not silently retry. Configuration changes fence in-flight results. The durable local queue and daily budget are documented in [category assistance](category-assistance.md).

## Evidence

Offline parity on the round-4 packet (60 cases, 120 recorded exchanges, no provider call): the production builder reproduces all 120 recorded request bodies byte for byte, and replaying the recorded responses through the production parser and decision gives an identical replay. At the default settings, production would complete 54 cases with two requests, abstain on 6 after stage 1 (1 no-fit, 5 uncertain-fit), write the root automatically for 27 and leave 27 as suggestions. Of the 27 automatic writes, 22 are item-stratum cases (21 with the owner's root), 2 are envelope-stratum cases (which no item root can match) and 3 are injection probes (none written to the injected root). Run `offline-production-v4` in `tools/Receipts.JevProductionParity` to repeat this.

The thresholds are pilot evidence from one 60-case cohort, not a validated operating point. Re-evaluate them, and leaf writes, on the restructured item-type tree with a fresh cohort.
