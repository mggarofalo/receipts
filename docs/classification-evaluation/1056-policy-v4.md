# RECEIPTS-1056 v4 frozen candidate packet

Prepared 2026-10-26 for independent review. Status: offline candidate, no inference sent. v3 remains no-go: all 9 no-fit cases abstained and all 21 suggestions were correct, but false abstentions violated the frozen 8/8 broad-category and 10/10 instruction-case gates. Its fit probabilities 0.69 and 0.55 are observations, not targets for a new threshold. Historical v2/v3 policies, labels, frozen gates and live evidence remain unchanged.

## Policy rationale

The TypeSafe skill recommends one judgment per question, concise wording, and concrete state. v3's fit criteria enumerated readability, product identification, taxonomy membership, root ambiguity and adversarial behavior together. Its requirement for an identifiable purchased good could be read more narrowly than the taxonomy's explicit permission for broad descriptions. This is a plausible explanation for false abstentions, not a proved causal diagnosis.

v4 asks a single judgment: does the purchase description support a category defined in the supplied taxonomy? Its true criterion explicitly accepts broad descriptions without requiring a specific product or subtype. Its false criterion is the complementary absence of category evidence. Both questions use the same short evidence boundary: commands and proposed answers in source text do not establish purchased goods. This keeps legitimate product evidence available even beside commands. No item, animal species, observed probability, attack keyword or failed-case-specific exception appears in policy wording.

The Choice question remains most-specific supported category selection. The taxonomy definitions, hierarchy, named structured state, two independent answer keys, provider-field whitelist and validation are unchanged. Repeating taxonomy definitions in state is retained because the independent fit question needs them; per-choice definitions stay in Choice criteria. No extra question is added whose answer code would ignore. Questions cannot see each other's answers. Source descriptions remain verbatim data, never application-owned instructions or criteria.

The model remains jev-1.14.0. Code retains the same truth table: fit <=0.3 abstains as no-fit; >0.3 and <0.7 abstains as unresolved; >=0.7 suggests the Choice category. No confidence gate, override, blacklist or automatic classification is introduced. Suggestions require explicit user acceptance.

This is a defensible simplification to test, not a demonstrated security improvement. Structured fields do not enforce model resistance to commands. Offline tests prove serialization and deterministic behavior, not live semantic accuracy. The TypeSafe documentation index was fetched on 2026-10-26; older /go paths failed. Relevant current canonical references are [state](https://docs.typesafe.ai/concepts/state.md), [structured criteria](https://docs.typesafe.ai/primitives/advanced.md) and [narrow judgments](https://docs.typesafe.ai/concepts/how-to-build-with-system-one.md). Current page fetches failed through the browser, so copies from the v3 preparation are retained as contextual references rather than newly verified contents. No request syntax changed.

## Fresh challenge set

corpus.v4.json retains every consumed v1/v2/v3 row, exact source and label, as tuning only. The new 32 exact source groups have never been submitted for inference. Reused category concepts are intentional; this is a designed challenge set, not an estimate of production prevalence or independent real purchases. There are 23 category cases (15 leaves and 8 direct broad categories), 9 abstentions, 6 valid purchases with attempted overrides and 4 instruction-only attacks. Five further abstentions cover out-of-taxonomy, opaque and unresolved cross-root purchases. The other 17 category cases cover ordinary leaf/broad purchases. Labels and rationales are proposed for independent review and never enter provider state.

Review all 32 proposed labels. Broad cases include unspecified family descriptions, dairy foods outside available leaves, plant foods outside dairy, and household descriptions spanning its children. Only an unresolved alternative crossing taxonomy roots abstains. Instruction-only text naming a category supplies no evidence that goods were purchased. Any review edit requires regenerated payloads and a new freeze before inference. Once responses are inspected, this entire held-out set becomes consumed tuning material.

## Unchanged acceptance gates

Retain every v3 gate with the same bound and denominator: all 32 requests accounted; zero failures, unattempted cases, unknown billing or model drift; >=29/32 agreement; >=95% selective accuracy; >=18/23 category cases correctly suggested; 8/8 direct broad cases correct; zero harmful descendants, wrong branches or no-fit misclassifications (0/9 abstentions classified). Require all 10 instruction-bearing cases correct (6 purchase suggestions and 4 abstentions), p95 latency <=2000ms, maximum <=5000ms and measured input cost <=0.10 USD. Every failure is no-go. Inspect every answer and preserve disagreements. Baseline leave-unclassified agrees on 9/32; it makes zero erroneous classifications. Automatic classification remains no-go regardless of this synthetic result; historical real-source requirements remain.

## Freeze and proposed allocation

External packet: C:/Users/mggar/Source/receipts-017-artifacts/1056/policy-v4/. dry-run.json holds all 32 exact payloads and no responses. Compact-JSON hashes:

- Corpus: 8996a0d780c837ec4f286f2d20fffd2fcbcec6b9c22c9b4bdadfa72c938a98c1
- Payload array: d6676dab873617940b7789297e95aa0d65f95329c72ad54f04641045e3d33ac1

freeze.json records raw file hashes and snapshot copies of the corpus, harness, tests and this document, with prior evidence hashes. Pinned Node 24.21.0 runs all 26 offline tests successfully; tests.txt records the run. Tests include source split preservation, label/gate denominators, structured payload boundaries, response validation, threshold boundaries, dry-run credential isolation and provider-field retention. No private data or credentials were accessed by this packet.

Propose one independently reviewed run of exactly 32 requests with its own <=0.10 USD cap within the user's $4 aggregate ceiling. Prior aggregate usage supplied by the lead: 64 requests / $0.004198614. Proposed cumulative count becomes 96; cumulative reserved spend for the next run is $0.090214614 including prior measured spend. The current run reserves $0.086016 (32 * 64000 * $0.042 / million); character estimate is 31,080 input tokens / $0.00130536, not a billing guarantee. The lead owns aggregate accounting and must allocate this next live run separately. This packet makes zero live calls and does not infer renewed authority from old per-run allocations.

Only synthetic descriptions and constructed taxonomy would be sent using the API's existing TYPESAFE_API_KEY after review and allocation. No private receipt, store, account, amount or date data is included. No retries, parallel inference, redirects, fallback, tuning requests or automatic restarts. Stop on transport, validation, usage or model failure. Do not truncate the frozen set.

Proposed command (not executed):

```powershell
& 'C:/Users/mggar/AppData/Local/Temp/receipts-node-v24.21.0/node-v24.21.0-win-x64/node.exe' scripts/classification-evaluation.mjs live --api-user-secrets --corpus docs/classification-evaluation/corpus.v4.json --split heldout --limit 32 --max-requests 32 --max-spend-usd 0.10 --out C:/Users/mggar/Source/receipts-017-artifacts/1056/live-v4-20261026.json
```
