# Jev suggestion adapter

Category assistance produces **user-confirmed suggestions only**. It never assigns a category, approves a remembered mapping, or rewrites a receipt automatically. Manual entry remains available without Jev.

`ICategorySuggestionProvider` accepts a description and the complete category snapshot: stable IDs, names, definitions, parent IDs, and revision. The application validates both the hierarchy and provider judgment. It returns a full category path, a valid abstention, or a separate `SuggestionFailure`. Provider errors are not no-fit answers. Acceptance rechecks the current source, assignment, taxonomy, consent, and connection revisions.

## Hierarchy policy

The policy is `classification-questions-v5` on `jev-1.14.0`. One HTTP request contains a global fit question, a choice among roots, and an independent choice for each internal category among itself and its immediate children. Parent-self permits a broad category when the evidence does not support a narrower one. All questions are answered independently; the reducer validates every answer before traversing the selected path.

Fit probability <= 0.3 abstains as `no-fit`, < 0.7 abstains as `uncertain-fit`, and >= 0.7 allows a suggestion. The returned category probability and confidence describe the final local choice (`ProbabilityScope=local_choice`), not the probability of the complete path. They are not multiplied together and are not an automatic acceptance threshold. Older stored flat-choice results retain `global_choice` scope.

The provider receives the description and category definitions, not payment or account details. Receipt text is untrusted evidence, not instructions. Validation checks the model, revision, known IDs, exact answer and criterion sets, normalized finite probability distributions, selected maximum, and nonnegative safe-integer token usage.

## Configuration and transport

Configure `TYPESAFE_API_KEY` through the API server's configuration/User Secrets path; see [Jev setup](jev-setup.md). `Jev:Enabled=false` disables connectivity. The registered assistance worker requires persisted administrator consent and current connection binding in addition to a configured credential. A successful connection check does not grant consent. The direct adapter's separate `Jev:InferenceEnabled` flag defaults to false; the registered worker uses its generation-bound dispatch authorization instead.

The client removes inherited resilience handlers and HTTP loggers, disables redirects, and makes one send with a 10-second linked timeout. Caller cancellation propagates. One singleton semaphore bounds concurrent transport use; busy attempts do not silently retry. Configuration changes fence in-flight results. The durable local queue and daily budget are documented in [category assistance](category-assistance.md).

The full hierarchy is sent or the request fails; it is never truncated. Each Choice supports at most 255 alternatives: up to 255 roots, or 254 immediate children plus their parent-self alternative. Total category count can exceed 255. Serialized requests are limited to 65,536 UTF-8 bytes and responses to 1 MiB. Byte limits do not establish model token limits for every possible taxonomy.

## Release evaluation

The frozen hierarchy evaluation used meaningful synthetic categories and the actual C# adapter. Across 32 attempts, 30 matched their fixed expected outcomes, one conservatively abstained where a broad category was expected, and one response was rejected as invalid. All 12 untouched holdouts matched. Two separate diagnostic requests did not reproduce the invalid response and do not replace that failure. No observed category path was wrong. This limited evidence supports human-reviewed assistance, not automatic assignment or a guarantee for arbitrary receipt descriptions.

The production category snapshot (309 active categories) serialized offline to 55,499 bytes without truncation. Boundary tests cover wider and deeper trees, malformed and oversized responses, authentication, timeout, cancellation, concurrency, configuration changes, and one-send behavior. Live size acceptance is recorded separately from offline byte measurements. Private receipt data and credentials are not part of the checked-in evaluation fixtures.