# Jev suggestion adapter

RECEIPTS-1057 introduces a server-side adapter contract for **user-confirmed suggestions only**. It does not classify purchases automatically, write receipt data, normalize items, expose an inference endpoint, or enable dependent UI.

`ICategorySuggestionProvider` accepts a description and a complete taxonomy snapshot: stable IDs, names, definitions, parent IDs, and revision. Application policy validates the hierarchy and provider judgment, then either returns a suggestion with its full path or a valid abstention. Failures have a separate `SuggestionFailure`; provider errors never become a no-fit answer. Consumers must check the current taxonomy revision again before accepting a suggestion.

The evaluated policy is `classification-questions-v4` on `jev-1.14.0`. Fit probability <= 0.3 abstains as `no-fit`, < 0.7 abstains as `uncertain-fit`, and >= 0.7 suggests the selected category. Direct parent suggestions are valid. Confidence is retained for provenance and is not an acceptance gate. The application validates model, revision, known IDs, complete normalized probability distributions, selected maximum, finite probabilities, and nonnegative safe-integer token usage before any suggestion or abstention.

## Configuration and transport

`TYPESAFE_API_KEY` uses the existing API configuration/User Secrets path. `Jev:Enabled` continues to govern administrative connectivity. **`Jev:InferenceEnabled` defaults to false** and is a separate explicit opt-in; a configured key or successful administrative check cannot enable inference. Both flags and a credential are required for the adapter's passive `IsAvailable` capability. This capability is not yet published through an API.

The inference client removes inherited resilience handlers and HTTP loggers, disables redirects, and sends one attempt with a 10-second linked timeout. Caller cancellation propagates. A shared singleton semaphore permits one in-flight request per server process and rejects additional requests as busy. There is no queue, retry, fallback, automatic restart, or provider error/body logging. Configuration changes during a request discard its result.

Requests reject invalid hierarchies, absent definitions, more than 255 categories, and serialized payloads above 12,000 UTF-8 bytes. The full tree is sent or the request fails; it is never silently truncated. Responses buffer at most 100,000 bytes. The conservative payload limit may reject real production taxonomies; no production-size accuracy or context-budget claim follows from the synthetic evaluation.

## Frozen semantics and validation

`tests/Infrastructure.Tests/Fixtures/jev-suggestion-v4.json` is an offline frozen request from the first heldout v5 synthetic case, produced by `payloadFor` in RECEIPTS-1056's unchanged harness. Its constructed grocery definitions are test input only. The adapter has no hardcoded grocery taxonomy. A semantic JSON comparison checks question wording, criteria, state shape, ID paths, display paths, and model against the fixture.

Source harness SHA-256: `9B388994543BFE3D352041A1F8D416C554D9BE0798531A9AF289F921E86FA1C6`.

Fixture SHA-256: `38012949092970C0A002D3923F4E2FCD3165C3C84AC62BE7CD4ED4978C8E4C1A`.

Offline tests cover policy boundaries, broad parent suggestions, no-fit, unknown IDs, malformed responses, model drift, usage validation, authentication rejection, rate limiting, timeouts, cancellation, payload bounds, shared concurrency and production dependency injection without inherited retries. Implementation tests made no live requests. A separately budgeted independent acceptance probe then exercised the actual C# adapter with four synthetic requests, including a 22-category UUID tree, a broad parent and no-fit input. All passed; usage was 16,615 input and 3,174 output tokens, estimated $0.000697830 under verified model pricing. Current official documentation was fetched during that review. These contract probes do not establish production accuracy or validate every user taxonomy.

Remaining delivery work includes independent code review, a separately allocated live adapter contract probe, production taxonomy sizing and definitions, authenticated capability/API contracts, UI gating and confirmation flow, stale-revision acceptance checks, and the standard combined checks. Automatic classification remains NO-GO.


