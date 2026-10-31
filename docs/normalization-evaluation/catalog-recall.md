# Catalog shortlist recall

RECEIPTS-1089. How often the correct catalog name is in the shortlist that
`NormalizationCandidateRetrieval.RetrieveAsync` returns. The catalog ChatGPT source-run
and the receipt-entry chooser can only choose from that shortlist, so this baseline is
what every later retrieval change (RECEIPTS-1094, 1095, 1096, 1097, 1100, 1102) is
judged by. It is a measurement, not a CI gate.

## Run it

```bash
RECEIPTS_RECALL_CONNECTION="Host=...;Port=...;Database=...;Username=...;Password=..." \
RECEIPTS_RECALL_OUTPUT=~/private/catalog-recall \
  dotnet run scripts/run-prerequisite-tests.cs -- catalog-recall
```

The lane needs Docker (the synthetic fixture runs in a throwaway PostgreSQL container),
verifies the pinned ONNX model, and measures the database named by
`RECEIPTS_RECALL_CONNECTION` read-only. The tests are
`tests/Infrastructure.IntegrationTests/CatalogRecall/CatalogRecallTests.cs`
(`Category=Integration`, `Prerequisite=CatalogRecall`).

The test log gets aggregates only. With `RECEIPTS_RECALL_OUTPUT` set, each run also writes
owner-only files there: the aggregate as Markdown and JSON, plus `private.*` files with
per-store numbers and per-source rows. Those private files contain receipt text. Never
commit them.

## What is measured

**Ground truth.** Each case is a source key (store, description, item code after
`NormalizationCatalogPolicy.Normalize`) with one expected target. There are two kinds,
which are never mixed in one number:

- `approved_mapping`: active, non-excluded `NormalizationSourceMappings`. These are
  human-approved and are the real baseline.
- `legacy_link`: **a proxy, not human-approved.** These are purchases the legacy
  resolver linked (`NormalizationOrigin = 'legacy_unknown'`). A source is used only
  when all of its purchases point at one active target.

**Leave-one-out.** Retrieval takes an internal `NormalizationRetrievalProbe` instead of
revoking rows, because revoking would fire `mapping_review_invalidation`. For an approved
case the probe does two things:

- It hides the case's own mapping from the `approved_example` channel. Mappings for the
  same description at other stores stay visible, as they are in production, and are
  reported as the `same_description_other_store` stratum.
- It hides the case's description from every candidate's displayed examples.

A legacy link is not evidence that any channel reads, so a proxy case hides nothing.
RECEIPTS-1102 adds rule (c), which excludes the receipt items linked through the evaluated
mapping.

**Modes.**

- *Catalog source-run*: `RetrieveAsync` with no filter.
- *Receipt chooser*: `RetrieveForReceiptEntryAsync`, the exact call the
  receipt-entry service makes.

**Metrics.**

- Recall@1, @5 and @20, and mean reciprocal rank.
- For hits: the share that carried each channel, and the share that carried only that channel.
- For misses: the share each channel alone would recover, meaning the channel found the
  target but final ordering or the 20-row cut dropped it.
- `semantic_unfiltered`: the target is among the 20 nearest neighbours before the
  chooser's evidence filter, which today discards semantic-only rows.

**Strata.**

- `readable` vs `abbreviated`. Readable means every product word (3+ letters, with
  `ReceiptSuggestionEvidence` non-product words removed) appears as a whole word in the
  target's display name. A description with no product word counts as abbreviated.
- `novel_description` vs `same_description_other_store`, by whether another evaluated
  source has the same description.
- `exact_name` vs `not_exact_name`, using the `exact` channel's own rule.
- `crowded_token`: some catalog-mode lexical token matches 20 or more active names.
- Anonymised store buckets by source count: `store_rank_1`, `store_rank_2_5` and
  `store_rank_6_plus`.

**Semantic floor evidence for RECEIPTS-1095.** In chooser mode the report gives the cosine
similarity of the expected target and of the nearest wrong active name, with percentiles
and a table of candidate floors. It also gives a second table without `exact_name`
sources, which sit at a similarity of about 1.

**Corpus state.** Every run records the embedding fingerprint and the share of active
catalog names embedded at it, and warns below 95%. That way a half-rebuilt corpus is not
mistaken for a model regression.

## Baseline: `main` at 99f5bfac (2026-10-31)

Local instance restored from the owner's portable backup of 2026-10-29 (export format 13)
and migrated to `main`. Embeddings were backfilled at the current fingerprint, and
catalog coverage was 100%.

### Approved mappings: none exist yet

The owner's data holds **zero** approved `NormalizationSourceMappings`. Every purchase
link is `legacy_unknown` (16,472) or `unassigned` (37). The approved-mapping run therefore
evaluates 0 sources. **Rerun this baseline once real mappings exist.** Until then, judge
retrieval changes by the synthetic fixture and by the proxy below, with the proxy's
caveats.

### Real data: legacy-link proxy (not human-approved)

Read this with three caveats:

1. **It covers only part of the data.** 668 sources qualify. 5,126 point at
   `PendingReview` names, which retrieval never returns, and 40 are ambiguous. The active
   catalog has 229 names.
2. **It is circular for `exact` and `semantic`.** The legacy resolver auto-accepted a link
   when cosine similarity reached the stored threshold (0.81). Many active names were also
   created from receipt text. So every non-exact proxy target sits at a similarity of at
   least ~0.82, and `semantic_unfiltered` recovers every miss by construction. Compare
   `exact_name` (372 sources, trivially solved) with `not_exact_name` (296).
3. **The receipt-chooser numbers are the informative ones.** The chooser drops
   semantic-only candidates, so its misses show what RECEIPTS-1095 can win back. Its
   similarity table is still a ceiling, not an estimate, for the reason in caveat 2.

#### Ground truth: `legacy_link`

PROXY, NOT HUMAN-APPROVED: purchases the legacy resolver linked (origin legacy_unknown). Many canonical names were created from receipt text, so exact and semantic are flattered; compare exact_name with not_exact_name.

- Measured: 2026-10-31 14:20 UTC
- Embedding fingerprint: `model=bge-large-en-v1.5|revision=d4aa6901d3a41ba39fb536a557fa166f842b0e09|model-sha256=69ed3f810d3b6d13f70dff9ca89966f39c0a0e877fb88211be7bcc070df2a2ce|onnx-runtime=1.24.3.0|tokenizer=Microsoft.ML.Tokenizers@1.0.0.0|vocab-sha256=07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3|max-tokens=512|pooling=CLS|normalization=l2-v1`
- Coverage: 229/229 active catalog names (100.0%)
- Sources evaluated: 668 (skipped: 5126 target not active, 40 ambiguous target, 0 invalid key)

##### Catalog source-run

| Group | Sources | R@1 | R@5 | R@20 | MRR |
| --- | ---: | ---: | ---: | ---: | ---: |
| all | 668 | 100.0% | 100.0% | 100.0% | 1.000 |
| readable | 503 | 100.0% | 100.0% | 100.0% | 1.000 |
| abbreviated | 165 | 100.0% | 100.0% | 100.0% | 1.000 |
| novel_description | 305 | 100.0% | 100.0% | 100.0% | 1.000 |
| same_description_other_store | 363 | 100.0% | 100.0% | 100.0% | 1.000 |
| crowded_token | 9 | 100.0% | 100.0% | 100.0% | 1.000 |
| exact_name | 372 | 100.0% | 100.0% | 100.0% | 1.000 |
| not_exact_name | 296 | 100.0% | 100.0% | 100.0% | 1.000 |
| store_rank_1 | 290 | 100.0% | 100.0% | 100.0% | 1.000 |
| store_rank_2_5 | 344 | 100.0% | 100.0% | 100.0% | 1.000 |
| store_rank_6_plus | 34 | 100.0% | 100.0% | 100.0% | 1.000 |

| Channel | Hits carrying | Hits only this | Misses it alone recovers |
| --- | ---: | ---: | ---: |
| exact | 55.7% | 0.0% | 0.0% |
| approved_example | 0.0% | 0.0% | 0.0% |
| lexical | 99.0% | 0.0% | 0.0% |
| semantic | 100.0% | 1.0% | 0.0% |
| semantic_unfiltered | — | — | 0.0% |

Misses: 0; no channel found the target for 0.0% of them.

##### Receipt chooser

| Group | Sources | R@1 | R@5 | R@20 | MRR |
| --- | ---: | ---: | ---: | ---: | ---: |
| all | 668 | 79.2% | 79.2% | 79.2% | 0.792 |
| readable | 503 | 89.9% | 89.9% | 89.9% | 0.899 |
| abbreviated | 165 | 46.7% | 46.7% | 46.7% | 0.467 |
| novel_description | 305 | 82.3% | 82.3% | 82.3% | 0.823 |
| same_description_other_store | 363 | 76.6% | 76.6% | 76.6% | 0.766 |
| crowded_token | 9 | 100.0% | 100.0% | 100.0% | 1.000 |
| exact_name | 372 | 100.0% | 100.0% | 100.0% | 1.000 |
| not_exact_name | 296 | 53.0% | 53.0% | 53.0% | 0.530 |
| store_rank_1 | 290 | 82.4% | 82.4% | 82.4% | 0.824 |
| store_rank_2_5 | 344 | 76.5% | 76.5% | 76.5% | 0.765 |
| store_rank_6_plus | 34 | 79.4% | 79.4% | 79.4% | 0.794 |

| Channel | Hits carrying | Hits only this | Misses it alone recovers |
| --- | ---: | ---: | ---: |
| exact | 70.3% | 0.0% | 0.0% |
| approved_example | 0.0% | 0.0% | 0.0% |
| lexical | 99.4% | 0.0% | 0.0% |
| semantic | 100.0% | 0.0% | 0.0% |
| semantic_unfiltered | — | — | 100.0% |

Misses: 139; no channel found the target for 0.0% of them.

Semantic similarity (cosine), expected target vs nearest wrong catalog name — all sources:

| Series | n | p10 | p25 | p50 | p75 | p90 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| target | 668 | 0.829 | 0.863 | 1.000 | 1.000 | 1.000 |
| top wrong | 668 | 0.603 | 0.634 | 0.656 | 0.707 | 0.728 |
| target − top wrong | 668 | 0.130 | 0.167 | 0.304 | 0.361 | 0.396 |

Target beats the nearest wrong name for 100.0%; 0 targets had no current vector.

| Floor | Targets ≥ floor | Top wrong ≥ floor | Chooser misses admitted |
| ---: | ---: | ---: | ---: |
| 0.60 | 100.0% | 90.4% | 100.0% |
| 0.65 | 100.0% | 62.0% | 100.0% |
| 0.70 | 100.0% | 28.4% | 100.0% |
| 0.75 | 100.0% | 2.7% | 100.0% |
| 0.80 | 100.0% | 0.1% | 100.0% |
| 0.85 | 81.7% | 0.0% | 31.7% |
| 0.90 | 63.0% | 0.0% | 7.2% |
| 0.95 | 56.9% | 0.0% | 2.2% |

Semantic similarity (cosine), expected target vs nearest wrong catalog name — not_exact_name:

| Series | n | p10 | p25 | p50 | p75 | p90 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| target | 296 | 0.822 | 0.831 | 0.857 | 0.883 | 0.916 |
| top wrong | 296 | 0.637 | 0.655 | 0.695 | 0.724 | 0.734 |
| target − top wrong | 296 | 0.115 | 0.130 | 0.166 | 0.199 | 0.259 |

Target beats the nearest wrong name for 100.0%; 0 targets had no current vector.

| Floor | Targets ≥ floor | Top wrong ≥ floor | Chooser misses admitted |
| ---: | ---: | ---: | ---: |
| 0.60 | 100.0% | 98.6% | 100.0% |
| 0.65 | 100.0% | 84.1% | 100.0% |
| 0.70 | 100.0% | 48.0% | 100.0% |
| 0.75 | 100.0% | 3.4% | 100.0% |
| 0.80 | 100.0% | 0.3% | 100.0% |
| 0.85 | 58.8% | 0.0% | 31.7% |
| 0.90 | 16.6% | 0.0% | 7.2% |
| 0.95 | 2.7% | 0.0% | 2.2% |

### Synthetic fixture (approved mappings, leave-one-out)

`tests/Fixtures/catalog-recall.synthetic.json` is wholly synthetic: 48 catalog names
(22 cheeses, to crowd one token) and 24 approved mappings. It covers every stratum,
including the same description approved at two stores for the same target and for
different targets. The test seeds it with real model vectors, as production does.

#### Ground truth: `approved_mapping`

Human-approved: active, non-excluded source mappings, leave-one-out.

- Measured: 2026-10-31 14:16 UTC
- Embedding fingerprint: `model=bge-large-en-v1.5|revision=d4aa6901d3a41ba39fb536a557fa166f842b0e09|model-sha256=69ed3f810d3b6d13f70dff9ca89966f39c0a0e877fb88211be7bcc070df2a2ce|onnx-runtime=1.24.3.0|tokenizer=Microsoft.ML.Tokenizers@1.0.0.0|vocab-sha256=07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3|max-tokens=512|pooling=CLS|normalization=l2-v1`
- Coverage: 48/48 active catalog names (100.0%)
- Sources evaluated: 24 (skipped: 0 target not active, 0 ambiguous target, 0 invalid key)

##### Catalog source-run

| Group | Sources | R@1 | R@5 | R@20 | MRR |
| --- | ---: | ---: | ---: | ---: | ---: |
| all | 24 | 83.3% | 100.0% | 100.0% | 0.906 |
| readable | 12 | 83.3% | 100.0% | 100.0% | 0.917 |
| abbreviated | 12 | 83.3% | 100.0% | 100.0% | 0.896 |
| novel_description | 20 | 90.0% | 100.0% | 100.0% | 0.938 |
| same_description_other_store | 4 | 50.0% | 100.0% | 100.0% | 0.750 |
| crowded_token | 3 | 100.0% | 100.0% | 100.0% | 1.000 |
| exact_name | 2 | 100.0% | 100.0% | 100.0% | 1.000 |
| not_exact_name | 22 | 81.8% | 100.0% | 100.0% | 0.898 |
| store_rank_1 | 9 | 66.7% | 100.0% | 100.0% | 0.806 |
| store_rank_2_5 | 15 | 93.3% | 100.0% | 100.0% | 0.967 |

| Channel | Hits carrying | Hits only this | Misses it alone recovers |
| --- | ---: | ---: | ---: |
| exact | 8.3% | 0.0% | 0.0% |
| approved_example | 8.3% | 0.0% | 0.0% |
| lexical | 91.7% | 0.0% | 0.0% |
| semantic | 100.0% | 8.3% | 0.0% |
| semantic_unfiltered | — | — | 0.0% |

Misses: 0; no channel found the target for 0.0% of them.

##### Receipt chooser

| Group | Sources | R@1 | R@5 | R@20 | MRR |
| --- | ---: | ---: | ---: | ---: | ---: |
| all | 24 | 50.0% | 50.0% | 50.0% | 0.500 |
| readable | 12 | 66.7% | 66.7% | 66.7% | 0.667 |
| abbreviated | 12 | 33.3% | 33.3% | 33.3% | 0.333 |
| novel_description | 20 | 50.0% | 50.0% | 50.0% | 0.500 |
| same_description_other_store | 4 | 50.0% | 50.0% | 50.0% | 0.500 |
| crowded_token | 3 | 66.7% | 66.7% | 66.7% | 0.667 |
| exact_name | 2 | 100.0% | 100.0% | 100.0% | 1.000 |
| not_exact_name | 22 | 45.5% | 45.5% | 45.5% | 0.455 |
| store_rank_1 | 9 | 44.4% | 44.4% | 44.4% | 0.444 |
| store_rank_2_5 | 15 | 53.3% | 53.3% | 53.3% | 0.533 |

| Channel | Hits carrying | Hits only this | Misses it alone recovers |
| --- | ---: | ---: | ---: |
| exact | 16.7% | 0.0% | 0.0% |
| approved_example | 16.7% | 0.0% | 0.0% |
| lexical | 100.0% | 0.0% | 0.0% |
| semantic | 100.0% | 0.0% | 0.0% |
| semantic_unfiltered | — | — | 100.0% |

Misses: 12; no channel found the target for 0.0% of them.

Semantic similarity (cosine), expected target vs nearest wrong catalog name — all sources:

| Series | n | p10 | p25 | p50 | p75 | p90 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| target | 24 | 0.668 | 0.738 | 0.788 | 0.879 | 0.940 |
| top wrong | 24 | 0.626 | 0.651 | 0.678 | 0.747 | 0.784 |
| target − top wrong | 24 | 0.002 | 0.037 | 0.084 | 0.130 | 0.215 |

Target beats the nearest wrong name for 87.5%; 0 targets had no current vector.

| Floor | Targets ≥ floor | Top wrong ≥ floor | Chooser misses admitted |
| ---: | ---: | ---: | ---: |
| 0.60 | 95.8% | 95.8% | 91.7% |
| 0.65 | 91.7% | 75.0% | 83.3% |
| 0.70 | 83.3% | 45.8% | 66.7% |
| 0.75 | 66.7% | 25.0% | 41.7% |
| 0.80 | 45.8% | 8.3% | 41.7% |
| 0.85 | 29.2% | 4.2% | 25.0% |
| 0.90 | 12.5% | 0.0% | 8.3% |
| 0.95 | 12.5% | 0.0% | 8.3% |

Semantic similarity (cosine), expected target vs nearest wrong catalog name — not_exact_name:

| Series | n | p10 | p25 | p50 | p75 | p90 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| target | 22 | 0.667 | 0.736 | 0.784 | 0.844 | 0.897 |
| top wrong | 22 | 0.626 | 0.646 | 0.663 | 0.735 | 0.782 |
| target − top wrong | 22 | -0.001 | 0.031 | 0.074 | 0.125 | 0.161 |

Target beats the nearest wrong name for 86.4%; 0 targets had no current vector.

| Floor | Targets ≥ floor | Top wrong ≥ floor | Chooser misses admitted |
| ---: | ---: | ---: | ---: |
| 0.60 | 95.5% | 95.5% | 91.7% |
| 0.65 | 90.9% | 72.7% | 83.3% |
| 0.70 | 81.8% | 45.5% | 66.7% |
| 0.75 | 63.6% | 22.7% | 41.7% |
| 0.80 | 40.9% | 9.1% | 41.7% |
| 0.85 | 22.7% | 4.5% | 25.0% |
| 0.90 | 4.5% | 0.0% | 8.3% |
| 0.95 | 4.5% | 0.0% | 8.3% |

## Reading the baseline

- **Catalog mode is saturated on this catalog.** With 229 active names, up to 8 lexical
  queries of 20 plus 20 semantic neighbours reach almost everything that shares a word.
  Recall@20 is 100% on both corpora. Rank (R@1 and MRR) is where catalog-mode changes
  will show.
- **The receipt chooser is the gap.** On the proxy it reaches 79.2% overall: 46.7% on
  abbreviated text and 53.0% on non-exact names. Every miss was among the 20 unfiltered
  semantic neighbours. On the synthetic fixture it reaches 50%.
- **Semantic floor (RECEIPTS-1095).** Within the proxy's non-exact sources, the nearest
  wrong name is at or above 0.75 for 3.4% of sources and at or above 0.80 for 0.3%. On
  the synthetic fixture's non-exact sources, where targets are not filtered by a threshold,
  the nearest wrong name is at or above 0.80 for 9.1% of sources. Only 40.9% of the correct targets reach
  0.80, and the margin over the nearest wrong name is small (median 0.074). A floor near
  0.80 keeps wrong admissions low on real catalog text. The synthetic numbers show it will
  still miss many true abbreviations. Choosing the floor belongs to RECEIPTS-1095.

## Recall history

Each retrieval change records its before and after numbers here and in its PR. The rows give
recall@20, with recall@1 in brackets where it moved, for the catalog source-run and the
receipt chooser. Fingerprint and coverage are unchanged unless stated.

| Change | Proxy catalog | Proxy chooser | Proxy chooser, abbreviated | Synthetic catalog | Synthetic chooser | Synthetic chooser, abbreviated |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Baseline, main 99f5bfac | 100% | 79.2% | 46.7% | 100% (83.3%) | 50.0% | 33.3% |
| RECEIPTS-1095 semantic floor 0.80 | 100% | 100% | 100% | 100% (83.3%) | 70.8% (62.5%) | 50.0% |

RECEIPTS-1095: the proxy chooser's jump is circular, because every proxy target sits at a
similarity of at least ~0.82 by construction. The synthetic fixture is the honest signal for
that change.
